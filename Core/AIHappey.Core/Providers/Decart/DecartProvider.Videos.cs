using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Decart;

public partial class DecartProvider
{
    private sealed class DecartVideoProviderMetadata
    {
        [JsonPropertyName("trajectory")]
        public JsonElement? Trajectory { get; set; }

        [JsonPropertyName("referenceImage")]
        public VideoFile? ReferenceImage { get; set; }

        [JsonPropertyName("enhancePrompt")]
        public bool? EnhancePrompt { get; set; }
    }

    private sealed record DecartJobState(string Status, JsonElement Root);

    public async Task<VideoResponse> DecartVideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        var model = request.Model.Trim();
        var endpoint = $"v1/jobs/{model}";
        var metadata = GetVideoProviderMetadata<DecartVideoProviderMetadata>(request, GetIdentifier());

        using var form = BuildDecartVideoForm(model, endpoint, request, metadata, warnings);

        using var createResp = await _client.PostAsync(endpoint, form, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Decart video request failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var jobId = createDoc.RootElement.TryGetProperty("job_id", out var jobIdEl) ? jobIdEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("Decart video request did not return job_id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollDecartJobAsync(jobId, ct),
            isTerminal: s => IsTerminalStatus(s.Status),
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (!string.Equals(completed.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            var error = TryReadErrorMessage(completed.Root);
            throw new InvalidOperationException($"Decart video job failed with status '{completed.Status}': {error}");
        }

        using var contentResp = await _client.GetAsync($"v1/jobs/{jobId}/content", cancellationToken);
        var videoBytes = await contentResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!contentResp.IsSuccessStatusCode)
        {
            var text = Encoding.UTF8.GetString(videoBytes);
            throw new InvalidOperationException($"Decart video download failed ({(int)contentResp.StatusCode}): {text}");
        }

        var mediaType = contentResp.Content.Headers.ContentType?.MediaType ?? "video/mp4";

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = mediaType,
                    Data = Convert.ToBase64String(videoBytes)
                }
            ],
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = completed.Root.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    endpoint,
                    jobId,
                    status = completed.Status,
                    mediaType,
                    bytes = videoBytes.Length
                }
            }
        };
    }

    private static MultipartFormDataContent BuildDecartVideoForm(
        string model,
        string endpoint,
        VideoRequest request,
        DecartVideoProviderMetadata? metadata,
        List<object> warnings)
    {
        var form = new MultipartFormDataContent();

        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration" });

        var supportsSeed = string.Equals(model, "lucy-motion", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "lucy-restyle-v2v", StringComparison.OrdinalIgnoreCase);

        if (request.Seed is not null)
        {
            if (supportsSeed)
                form.Add(new StringContent(request.Seed.Value.ToString()), "seed");
            else
                warnings.Add(new { type = "unsupported", feature = "seed" });
        }

        form.Add(new StringContent(ResolveVideoResolution(request, warnings)), "resolution");

        var isTextToVideo = string.Equals(model, "lucy-pro-t2v", StringComparison.OrdinalIgnoreCase);
        var isImageToVideo = string.Equals(model, "lucy-pro-i2v", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "lucy-dev-i2v", StringComparison.OrdinalIgnoreCase);
        var isMotion = string.Equals(model, "lucy-motion", StringComparison.OrdinalIgnoreCase);
        var isVideoToVideo = string.Equals(model, "lucy-pro-v2v", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "lucy-fast-v2v", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "lucy-restyle-v2v", StringComparison.OrdinalIgnoreCase);

        if (isTextToVideo)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("Prompt is required for text-to-video.", nameof(request));

            form.Add(new StringContent(request.Prompt), "prompt");

            if (request.Image is not null)
                warnings.Add(new { type = "unsupported", feature = "image" });
        }
        else if (isImageToVideo)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("Prompt is required for image-to-video.", nameof(request));

            var input = request.Image ?? throw new ArgumentException("Image input is required for image-to-video.", nameof(request));
            form.Add(ToByteArrayContent(input, requiredPrefix: "image/"), "data", "input-image");
            form.Add(new StringContent(request.Prompt), "prompt");
        }
        else if (isMotion)
        {
            var input = request.Image ?? throw new ArgumentException("Image input is required for lucy-motion.", nameof(request));
            form.Add(ToByteArrayContent(input, requiredPrefix: "image/"), "data", "input-image");

            if (metadata?.Trajectory is null || metadata.Trajectory.Value.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Trajectory is required for lucy-motion in providerOptions.decart.trajectory.", nameof(request));

            form.Add(new StringContent(metadata.Trajectory.Value.GetRawText(), Encoding.UTF8, MediaTypeNames.Application.Json), "trajectory");

            if (!string.IsNullOrWhiteSpace(request.Prompt))
                warnings.Add(new { type = "unsupported", feature = "prompt" });
        }
        else if (isVideoToVideo)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("Prompt is required for video-to-video models.", nameof(request));

            var input = request.Image ?? throw new ArgumentException("Video input is required for video-to-video models.", nameof(request));
            form.Add(ToByteArrayContent(input, requiredPrefix: "video/"), "data", "input-video");
            form.Add(new StringContent(request.Prompt), "prompt");

            if (metadata?.ReferenceImage is not null)
            {
                if (string.Equals(model, "lucy-pro-v2v", StringComparison.OrdinalIgnoreCase))
                {
                    form.Add(ToByteArrayContent(metadata.ReferenceImage, requiredPrefix: "image/"), "reference_image", "reference-image");
                }
                else
                {
                    warnings.Add(new { type = "unsupported", feature = "reference_image" });
                }
            }

            if (metadata?.EnhancePrompt is not null)
            {
                if (string.Equals(model, "lucy-restyle-v2v", StringComparison.OrdinalIgnoreCase))
                    form.Add(new StringContent(metadata.EnhancePrompt.Value ? "true" : "false"), "enhance_prompt");
                else
                    warnings.Add(new { type = "unsupported", feature = "enhance_prompt" });
            }
        }
        else
        {
            throw new NotSupportedException($"Unsupported Decart endpoint '{endpoint}'.");
        }

        return form;
    }

    private async Task<DecartJobState> PollDecartJobAsync(string jobId, CancellationToken cancellationToken)
    {
        using var pollResp = await _client.GetAsync($"v1/jobs/{jobId}", cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Decart job poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString() ?? "unknown"
            : "unknown";

        return new DecartJobState(status, root);
    }

    private static bool IsTerminalStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static string TryReadErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
            return errorEl.GetString() ?? "Unknown error";

        if (root.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String)
            return messageEl.GetString() ?? "Unknown error";

        return "Unknown error";
    }

    private static ByteArrayContent ToByteArrayContent(VideoFile file, string requiredPrefix)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (string.IsNullOrWhiteSpace(file.MediaType))
            throw new ArgumentException("MediaType is required for input files.", nameof(file));

        if (!file.MediaType.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Expected media type starting with '{requiredPrefix}'.", nameof(file));

        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Input data is required.", nameof(file));

        if (file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Decart video only supports base64 content, not URLs.", nameof(file));
        }

        var base64 = file.Data;
        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = file.Data.IndexOf(',');
            if (comma < 0)
                throw new ArgumentException("Invalid data URL format.", nameof(file));

            base64 = file.Data[(comma + 1)..];
        }

        var bytes = Convert.FromBase64String(base64);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(file.MediaType);
        return content;
    }

    private static string ResolveVideoResolution(VideoRequest request, List<object> warnings)
    {
        if (!string.IsNullOrWhiteSpace(request.Resolution))
        {
            var normalized = request.Resolution.Trim();
            if (string.Equals(normalized, "480p", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "720p", StringComparison.OrdinalIgnoreCase))
            {
                return normalized.ToLowerInvariant();
            }

            if (TryResolveResolutionFromSize(normalized, out var bySize))
                return bySize;

            warnings.Add(new { type = "unsupported", feature = "resolution" });
        }

        if (TryResolveResolutionFromAspectRatio(request.AspectRatio, out var byAspectRatio))
            return byAspectRatio;

        return "720p";
    }

    private static T? GetVideoProviderMetadata<T>(VideoRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return default;

        if (!request.ProviderOptions.TryGetValue(providerId, out var element))
            return default;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }
}

