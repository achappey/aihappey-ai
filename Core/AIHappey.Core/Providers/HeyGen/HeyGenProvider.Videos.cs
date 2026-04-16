using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.HeyGen;

public partial class HeyGenProvider
{
    private sealed record HeyGenVideoStatusPollResult(
        bool IsTerminal,
        bool IsCompleted,
        string? Status,
        JsonElement Root,
        string Raw);

    private async Task<VideoResponse> HeyGenVideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        AddUnsupportedVideoWarnings(request, warnings);

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildVideoAgentGeneratePayload(request.Prompt, metadata);

        var generateRaw = await PostJsonAndReadAsync("v1/video_agent/generate", payload, cancellationToken);

        using var generateDoc = JsonDocument.Parse(generateRaw);
        EnsureNoHeyGenVideoApiError(generateDoc.RootElement, generateRaw);

        var generateData = GetHeyGenVideoDataElement(generateDoc.RootElement);
        var videoId = ReadString(generateData, "video_id")
            ?? ReadString(generateData, "videoId")
            ?? ReadString(generateDoc.RootElement, "video_id")
            ?? ReadString(generateDoc.RootElement, "videoId")
            ?? throw new InvalidOperationException($"{ProviderName} video create response missing video_id: {generateRaw}");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollHeyGenVideoStatusAsync(videoId, ct),
            isTerminal: result => result.IsTerminal,
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (!completed.IsCompleted)
            throw new InvalidOperationException($"{ProviderName} video generation failed with status '{completed.Status ?? "unknown"}': {completed.Raw}");

        var statusData = GetHeyGenVideoDataElement(completed.Root);
        var videoUrl = ReadString(statusData, "video_url")
            ?? ReadString(statusData, "videoUrl")
            ?? throw new InvalidOperationException($"{ProviderName} completed video status response missing video_url: {completed.Raw}");

        using var videoResp = await _client.GetAsync(videoUrl, cancellationToken);
        var videoBytes = await videoResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} video download failed ({(int)videoResp.StatusCode}): {Encoding.UTF8.GetString(videoBytes)}");

        var mediaType = videoResp.Content.Headers.ContentType?.MediaType
            ?? GuessHeyGenVideoMediaType(videoUrl)
            ?? "video/mp4";

        var providerMetadata = new JsonObject
        {
            ["generate_endpoint"] = "v1/video_agent/generate",
            ["status_endpoint"] = "v1/video_status.get",
            ["video_id"] = videoId,
            ["status"] = completed.Status,
            ["video_url"] = videoUrl,
            ["content_type"] = mediaType,
            ["create_response"] = JsonNode.Parse(generateDoc.RootElement.GetRawText()),
            ["status_response"] = JsonNode.Parse(completed.Root.GetRawText())
        };

        if (metadata.ValueKind == JsonValueKind.Object)
            providerMetadata["passthrough"] = JsonNode.Parse(metadata.GetRawText());

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
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMetadata, JsonSerializerOptions.Web)
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    video_id = videoId,
                    status = completed.Status,
                    contentType = mediaType,
                    bytes = videoBytes.Length
                }
            }
        };
    }

    private async Task<HeyGenVideoStatusPollResult> PollHeyGenVideoStatusAsync(string videoId, CancellationToken cancellationToken)
    {
        var path = $"v1/video_status.get?video_id={Uri.EscapeDataString(videoId)}";
        using var response = await _client.GetAsync(path, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} video status request failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        EnsureNoHeyGenVideoApiError(root, raw);

        var data = GetHeyGenVideoDataElement(root);
        var status = ReadString(data, "status") ?? ReadString(root, "status");
        var normalized = status?.Trim().ToLowerInvariant();

        var isCompleted = string.Equals(normalized, "completed", StringComparison.Ordinal);
        var isFailed = string.Equals(normalized, "failed", StringComparison.Ordinal);
        var isPending = string.Equals(normalized, "pending", StringComparison.Ordinal);
        var isProcessing = string.Equals(normalized, "processing", StringComparison.Ordinal);

        var isTerminal = isCompleted || isFailed;
        if (!isTerminal && !isPending && !isProcessing)
            throw new InvalidOperationException($"{ProviderName} returned unknown video status '{status ?? "null"}': {raw}");

        return new HeyGenVideoStatusPollResult(isTerminal, isCompleted, normalized, root, raw);
    }

    private static JsonObject BuildVideoAgentGeneratePayload(string prompt, JsonElement metadata)
    {
        var payload = metadata.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(metadata.GetRawText()) as JsonObject ?? []
            : [];

        if (!payload.ContainsKey("prompt"))
            payload["prompt"] = prompt.Trim();

        var effectivePromptNode = payload["prompt"];
        var effectivePrompt = effectivePromptNode is JsonValue value && value.TryGetValue<string>(out var promptFromPayload)
            ? promptFromPayload
            : null;

        if (string.IsNullOrWhiteSpace(effectivePrompt))
            throw new ArgumentException("Prompt is required.", nameof(prompt));

        return payload;
    }

    private static void AddUnsupportedVideoWarnings(VideoRequest request, List<object> warnings)
    {
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            warnings.Add(new { type = "unsupported", feature = "resolution" });

        if (request.Image is not null)
            warnings.Add(new { type = "unsupported", feature = "image" });
    }

    private static JsonElement GetHeyGenVideoDataElement(JsonElement root)
        => TryGetPropertyIgnoreCase(root, "data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data
            : root;

    private static void EnsureNoHeyGenVideoApiError(JsonElement root, string raw)
    {
        if (!TryGetPropertyIgnoreCase(root, "error", out var error)
            || error.ValueKind == JsonValueKind.Null
            || error.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }

        if (error.ValueKind == JsonValueKind.String)
        {
            var message = error.GetString();
            if (!string.IsNullOrWhiteSpace(message))
                throw new InvalidOperationException($"{ProviderName} API error: {message}. Raw: {raw}");

            return;
        }

        if (error.ValueKind == JsonValueKind.Object)
        {
            var code = ReadString(error, "code");
            var message = ReadString(error, "message") ?? "Unknown HeyGen error";

            throw new InvalidOperationException(string.IsNullOrWhiteSpace(code)
                ? $"{ProviderName} API error: {message}. Raw: {raw}"
                : $"{ProviderName} API error ({code}): {message}. Raw: {raw}");
        }

        throw new InvalidOperationException($"{ProviderName} API error: {error.GetRawText()}. Raw: {raw}");
    }

    private static string? GuessHeyGenVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";
        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";

        return null;
    }
}
