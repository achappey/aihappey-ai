using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Decart;

public partial class DecartProvider
{
    private const string DecartVideoOperationTokenPrefix = "dcv1_";

    private sealed record DecartJobState(string Status, JsonElement Root);
    private sealed record DecartVideoOperationData(string JobId, string? Model);

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
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
        var metadata = GetDecartProviderOptions(request.ProviderOptions, GetIdentifier());

        using var form = BuildDecartVideoForm(request, metadata, warnings);

        using var createResp = await _client.PostAsync(endpoint, form, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Decart video request failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var jobId = createDoc.RootElement.TryGetProperty("job_id", out var jobIdEl) ? jobIdEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("Decart video request did not return job_id.");

        var createRoot = createDoc.RootElement.Clone();
        return new VideoOperationStartResult
        {
            Operation = EncodeDecartVideoOperation(jobId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { jobId, status = "queued", job = createRoot }),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeDecartVideoOperation(operation);
        var jobId = operationData.JobId;
        ApplyAuthHeader();
        var job = await PollDecartJobAsync(jobId, cancellationToken);
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { jobId, status = job.Status, job = job.Root });
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = string.IsNullOrWhiteSpace(operationData.Model)
                ? GetIdentifier()
                : operationData.Model.ToModelId(GetIdentifier())
        };

        if (!string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            if (IsTerminalStatus(job.Status))
                return new VideoOperationErrorResult { Error = $"Decart video job failed with status '{job.Status}': {TryReadErrorMessage(job.Root)}", ProviderMetadata = metadata, Response = response };

            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };
        }

        using var contentResp = await _client.GetAsync($"v1/jobs/{Uri.EscapeDataString(jobId)}/content", cancellationToken);
        var videoBytes = await contentResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!contentResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Decart video download failed ({(int)contentResp.StatusCode}): {Encoding.UTF8.GetString(videoBytes)}");

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData { Type = "base64", MediaType = contentResp.Content.Headers.ContentType?.MediaType ?? "video/mp4", Data = Convert.ToBase64String(videoBytes) }],
            Warnings = [], ProviderMetadata = metadata, Response = response
        };
    }

    private static string EncodeDecartVideoOperation(string jobId, string model)
    {
        var json = JsonSerializer.Serialize(new DecartVideoOperationData(jobId, model), JsonSerializerOptions.Web);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return DecartVideoOperationTokenPrefix + base64Url;
    }

    private static DecartVideoOperationData DecodeDecartVideoOperation(string operation)
    {
        if (!operation.StartsWith(DecartVideoOperationTokenPrefix, StringComparison.Ordinal))
            return new DecartVideoOperationData(Uri.UnescapeDataString(operation), null);

        var base64Url = operation[DecartVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<DecartVideoOperationData>(json, JsonSerializerOptions.Web);
            if (data is null || string.IsNullOrWhiteSpace(data.JobId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The Decart video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The Decart video operation token is invalid.", nameof(operation), ex);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The Decart video operation token is invalid.", nameof(operation), ex);
        }
    }

    private static MultipartFormDataContent BuildDecartVideoForm(
        VideoRequest request,
        JsonElement metadata,
        List<object> warnings)
    {
        var form = new MultipartFormDataContent();

        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration" });

        var inputs = new[] { request.Image }
            .Concat(request.InputReferences ?? [])
            .Where(file => file is not null)
            .Cast<VideoFile>()
            .ToArray();
        var video = inputs.FirstOrDefault(file => file.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
            ?? throw new ArgumentException("A video input is required in image or inputReferences.", nameof(request));

        form.Add(ToByteArrayContent(video, requiredPrefix: "video/"), "data", "input-video");

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            form.Add(new StringContent(request.Prompt, Encoding.UTF8), "prompt");
        if (request.Seed is not null)
            form.Add(new StringContent(request.Seed.Value.ToString()), "seed");
        form.Add(new StringContent(ResolveVideoResolution(request, warnings)), "resolution");

        var reference = inputs.FirstOrDefault(file => file.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true);
        if (reference is not null)
            form.Add(ToByteArrayContent(reference, requiredPrefix: "image/"), "reference_image", "reference-image");

        AddDecartBooleanOption(form, metadata, "enhance_prompt");
        AddDecartBooleanOption(form, metadata, "self_anchor");

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

    private static JsonElement GetDecartProviderOptions(Dictionary<string, JsonElement>? providerOptions, string providerId)
    {
        if (providerOptions is not null
            && providerOptions.TryGetValue(providerId, out var element)
            && element.ValueKind == JsonValueKind.Object)
            return element;

        return default;
    }

    private static void AddDecartBooleanOption(MultipartFormDataContent form, JsonElement options, string name)
    {
        if (options.ValueKind != JsonValueKind.Object
            || !options.TryGetProperty(name, out var value)
            || value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            return;

        form.Add(new StringContent(value.GetBoolean() ? "true" : "false"), name);
    }
}

