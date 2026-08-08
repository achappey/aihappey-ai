using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
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

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
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

        return new VideoOperationStartResult
        {
            Operation = videoId,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { videoId, status = "pending" }),
            Response = new ()
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

        ApplyAuthHeader();
        var result = await PollHeyGenVideoStatusAsync(operation, cancellationToken);
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { videoId = operation, status = result.Status });
        var response = new HeaderResponseData { Timestamp = DateTime.UtcNow, ModelId = GetIdentifier() };

        if (result.IsTerminal && !result.IsCompleted)
            return new VideoOperationErrorResult { Error = $"{ProviderName} video generation failed with status '{result.Status ?? "unknown"}': {result.Raw}", ProviderMetadata = metadata, Response = response };

        if (!result.IsCompleted)
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        var statusData = GetHeyGenVideoDataElement(result.Root);
        var videoUrl = ReadString(statusData, "video_url") ?? ReadString(statusData, "videoUrl");
        if (string.IsNullOrWhiteSpace(videoUrl))
            return new VideoOperationErrorResult { Error = $"{ProviderName} completed video status response missing video_url: {result.Raw}", ProviderMetadata = metadata, Response = response };

        using var videoResp = await _client.GetAsync(videoUrl, cancellationToken);
        var videoBytes = await videoResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} video download failed ({(int)videoResp.StatusCode}): {Encoding.UTF8.GetString(videoBytes)}");

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData { Type = "base64", MediaType = videoResp.Content.Headers.ContentType?.MediaType ?? GuessHeyGenVideoMediaType(videoUrl) ?? "video/mp4", Data = Convert.ToBase64String(videoBytes) }],
            Warnings = [], ProviderMetadata = metadata, Response = response
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
