using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Apertis;

public partial class ApertisProvider
{
    private static readonly JsonSerializerOptions ApertisVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildApertisVideoCreatePayload(request, metadata, warnings);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/video/create")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ApertisVideoJsonOptions),
                Encoding.UTF8,
                MediaTypeHeaderValue.Parse(MediaTypeNames.Application.Json))
        };

        using var createResponse = await _client.SendAsync(createRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"Apertis video create failed ({(int)createResponse.StatusCode})."
                : $"Apertis video create failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();
        var taskId = ApertisTryGetString(createRoot, "id");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Apertis video create response did not contain a task id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async token =>
            {
                using var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"v1/video/query?id={Uri.EscapeDataString(taskId)}");
                using var pollResponse = await _client.SendAsync(pollRequest, HttpCompletionOption.ResponseHeadersRead, token);
                var pollRaw = await pollResponse.Content.ReadAsStringAsync(token);

                if (!pollResponse.IsSuccessStatusCode)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(pollRaw)
                        ? $"Apertis video poll failed ({(int)pollResponse.StatusCode})."
                        : $"Apertis video poll failed ({(int)pollResponse.StatusCode}): {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return (root: pollDoc.RootElement.Clone(), raw: pollRaw, headers: pollResponse.GetHeaders());
            },
            result => IsApertisVideoTerminal(ApertisTryGetString(result.root, "status")),
            interval: TimeSpan.FromSeconds(10),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var finalStatus = ApertisTryGetString(completed.root, "status");
        if (!string.Equals(finalStatus, "completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Apertis video generation failed with status '{finalStatus ?? "unknown"}': {completed.root.GetRawText()}");

        var videoUrl = ApertisTryGetString(completed.root, "video_url", "url");
        if (string.IsNullOrWhiteSpace(videoUrl))
            throw new InvalidOperationException("Apertis video completion response did not contain a video_url.");

        using var videoResponse = await _client.GetAsync(videoUrl, cancellationToken);
        var videoBytes = await videoResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResponse.IsSuccessStatusCode || videoBytes.Length == 0)
            throw new InvalidOperationException($"Failed to download Apertis video from returned URL ({(int)videoResponse.StatusCode}).");

        var mediaType = videoResponse.Content.Headers.ContentType?.MediaType
            ?? GuessApertisVideoMediaType(videoUrl)
            ?? "video/mp4";

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
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = ReadApertisUnixTimestamp(completed.root, "status_update_time") ?? now,
                Headers = completed.headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static Dictionary<string, object?> BuildApertisVideoCreatePayload(VideoRequest request, JsonElement metadata, List<object> warnings)
    {
        var payload = ApertisJsonObjectToDictionary(metadata);

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["size"] = request.Resolution;
        if (request.Duration.HasValue)
            payload["duration"] = request.Duration.Value;

        var images = ResolveApertisVideoImages(request).ToList();
        if (images.Count > 0)
            payload["images"] = images;

        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed", details = "Apertis video create does not document seed." });
        if (request.Fps.HasValue)
            warnings.Add(new { type = "unsupported", feature = "fps", details = "Apertis video create does not document fps." });
        if (request.N.HasValue)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Apertis video create returns a single async task." });

        return payload;
    }

    private static IEnumerable<string> ResolveApertisVideoImages(VideoRequest request)
    {
        if (request.Image is not null)
            yield return NormalizeApertisVideoImage(request.Image);

        if (request.InputReferences is not null)
        {
            foreach (var reference in request.InputReferences)
                yield return NormalizeApertisVideoImage(reference);
        }

        if (request.FrameImages is not null)
        {
            foreach (var frame in request.FrameImages)
                if (frame?.Image is not null)
                    yield return NormalizeApertisVideoImage(frame.Image);
        }
    }

    private static string NormalizeApertisVideoImage(VideoFile file)
    {
        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Image.Png
            : file.MediaType;

        return file.Data.ToDataUrl(mediaType);
    }

    private static bool IsApertisVideoTerminal(string? status)
        => status is not null && (
            status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase));

    private static string? GuessApertisVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var normalized = url.Trim().ToLowerInvariant();
        if (normalized.Contains(".webm")) return "video/webm";
        if (normalized.Contains(".mov")) return "video/quicktime";
        if (normalized.Contains(".mkv")) return "video/x-matroska";
        if (normalized.Contains(".avi")) return "video/x-msvideo";
        if (normalized.Contains(".mp4")) return "video/mp4";

        return null;
    }
}
