using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.Venice;

public partial class VeniceProvider
{
    private sealed record VeniceRetrievePollResult(
        bool IsCompleted,
        byte[]? VideoBytes,
        string? MediaType,
        JsonElement? JsonBody);

    private async Task<VideoResponse> VeniceVideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var queuePayload = BuildQueuePayload(request, metadata, warnings);
        var queueRaw = await QueueVideoAsync(queuePayload, cancellationToken);

        using var queueDoc = JsonDocument.Parse(queueRaw);
        var queueRoot = queueDoc.RootElement.Clone();
        var queueId = TryGetString(queueRoot, "queue_id")
            ?? throw new InvalidOperationException("Venice /v1/video/queue response missing queue_id.");

        var queuedModel = TryGetString(queueRoot, "model") ?? request.Model.Trim();
        var retrievePayload = BuildRetrievePayload(queuedModel, queueId, metadata);

        var pollIntervalSeconds = ResolvePollIntervalSeconds(metadata);
        var pollTimeoutMinutes = ResolvePollTimeoutMinutes(metadata);
        var pollMaxAttempts = ResolvePollMaxAttempts(metadata);

        var pollResult = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => RetrieveVideoUntilCompletedAsync(retrievePayload, ct),
            isTerminal: r => r.IsCompleted,
            interval: TimeSpan.FromSeconds(pollIntervalSeconds),
            timeout: TimeSpan.FromMinutes(pollTimeoutMinutes),
            maxAttempts: pollMaxAttempts,
            cancellationToken: cancellationToken);

        var videoBytes = pollResult.VideoBytes
            ?? throw new InvalidOperationException("Venice video retrieval completed without video bytes.");

        var mediaType = !string.IsNullOrWhiteSpace(pollResult.MediaType)
            ? pollResult.MediaType!
            : "video/mp4";

        var providerMetadata = new JsonObject
        {
            ["queue_endpoint"] = "v1/video/queue",
            ["retrieve_endpoint"] = "v1/video/retrieve",
            ["queue_response"] = JsonNode.Parse(queueRoot.GetRawText()),
            ["retrieve_response"] = pollResult.JsonBody is { } finalBody
                ? JsonNode.Parse(finalBody.GetRawText())
                : null,
            ["delete_media_on_completion"] = ReadDeleteAfterDownload(retrievePayload)
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
                ModelId = queuedModel,
                Body = new
                {
                    queue_id = queueId,
                    contentType = mediaType,
                    bytes = videoBytes.Length
                }
            }
        };
    }

    private async Task<string> QueueVideoAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/video/queue")
        {
            Content = new StringContent(payload.ToJsonString(JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Venice video queue request failed ({(int)response.StatusCode}): {raw}");

        return raw;
    }

    private async Task<VeniceRetrievePollResult> RetrieveVideoUntilCompletedAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/video/retrieve")
        {
            Content = new StringContent(payload.ToJsonString(JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var rawError = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Venice video retrieve request failed ({(int)response.StatusCode}): {rawError}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(mediaType)
            && mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new VeniceRetrievePollResult(true, bytes, mediaType, null);
        }

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var status = TryGetString(root, "status");

        if (string.Equals(status, "PROCESSING", StringComparison.OrdinalIgnoreCase))
            return new VeniceRetrievePollResult(false, null, null, root);

        // Defensive fallback for providers returning a URL in JSON instead of direct binary body.
        var url = TryGetString(root, "video_url")
            ?? TryGetString(root, "url");

        if (!string.IsNullOrWhiteSpace(url))
        {
            using var videoResp = await _client.GetAsync(url, cancellationToken);
            var videoBytes = await videoResp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!videoResp.IsSuccessStatusCode)
            {
                var err = Encoding.UTF8.GetString(videoBytes);
                throw new InvalidOperationException($"Venice video download failed ({(int)videoResp.StatusCode}): {err}");
            }

            var videoMediaType = videoResp.Content.Headers.ContentType?.MediaType
                ?? GuessVideoMediaType(url)
                ?? "video/mp4";

            return new VeniceRetrievePollResult(true, videoBytes, videoMediaType, root);
        }

        throw new InvalidOperationException($"Venice retrieve returned unexpected payload: {raw}");
    }

    private static JsonObject BuildQueuePayload(VideoRequest request, JsonElement metadata, List<object> warnings)
    {
        var payload = CreateQueuePayloadFromMetadata(metadata);

        SetIfMissing(payload, "model", request.Model?.Trim());
        SetIfMissing(payload, "prompt", request.Prompt?.Trim());

        if (request.Duration is not null && !payload.ContainsKey("duration"))
            payload["duration"] = NormalizeDuration(request.Duration.Value, warnings);

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            SetIfMissing(payload, "aspect_ratio", request.AspectRatio.Trim());

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            SetIfMissing(payload, "resolution", request.Resolution.Trim());

        if (request.Image is not null)
        {
            var inputField = ResolveInputField(request.Image.MediaType);
            if (!payload.ContainsKey(inputField))
                payload[inputField] = NormalizeVideoInput(request.Image);
        }

        if (!payload.ContainsKey("model"))
            throw new ArgumentException("Model is required.", nameof(request));

        if (!payload.ContainsKey("prompt"))
            throw new ArgumentException("Prompt is required.", nameof(request));

        if (!payload.ContainsKey("duration"))
            throw new ArgumentException("Duration is required. Set request.Duration or provider metadata.duration.", nameof(request));

        return payload;
    }

    private static JsonObject BuildRetrievePayload(string model, string queueId, JsonElement metadata)
    {
        var payload = CreateRetrievePayloadFromMetadata(metadata);
        SetIfMissing(payload, "model", model);
        SetIfMissing(payload, "queue_id", queueId);

        if (!payload.ContainsKey("delete_media_on_completion"))
            payload["delete_media_on_completion"] = true;

        return payload;
    }

    private static JsonObject CreateQueuePayloadFromMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return [];

        if (metadata.TryGetProperty("queue", out var queueNode)
            && queueNode.ValueKind == JsonValueKind.Object)
        {
            return JsonNode.Parse(queueNode.GetRawText()) as JsonObject ?? [];
        }

        var payload = JsonNode.Parse(metadata.GetRawText()) as JsonObject ?? [];
        payload.Remove("retrieve");
        payload.Remove("poll_interval_seconds");
        payload.Remove("poll_timeout_minutes");
        payload.Remove("poll_max_attempts");
        return payload;
    }

    private static JsonObject CreateRetrievePayloadFromMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return [];

        if (metadata.TryGetProperty("retrieve", out var retrieveNode)
            && retrieveNode.ValueKind == JsonValueKind.Object)
        {
            return JsonNode.Parse(retrieveNode.GetRawText()) as JsonObject ?? [];
        }

        var payload = new JsonObject();
        if (metadata.TryGetProperty("delete_media_on_completion", out var deleteNode)
            && (deleteNode.ValueKind == JsonValueKind.True || deleteNode.ValueKind == JsonValueKind.False))
        {
            payload["delete_media_on_completion"] = deleteNode.GetBoolean();
        }

        return payload;
    }

    private static bool ReadDeleteAfterDownload(JsonObject payload)
    {
        if (payload.TryGetPropertyValue("delete_media_on_completion", out var node)
            && node is JsonValue value
            && value.TryGetValue<bool>(out var parsed))
        {
            return parsed;
        }

        return true;
    }

    private static int ResolvePollIntervalSeconds(JsonElement metadata)
    {
        var value = VeniceVideoTryGetInt(metadata, "poll_interval_seconds");
        return value is > 0 ? value.Value : 2;
    }

    private static int ResolvePollTimeoutMinutes(JsonElement metadata)
    {
        var value = VeniceVideoTryGetInt(metadata, "poll_timeout_minutes");
        return value is > 0 ? value.Value : 10;
    }

    private static int? ResolvePollMaxAttempts(JsonElement metadata)
    {
        var value = VeniceVideoTryGetInt(metadata, "poll_max_attempts");
        return value is > 0 ? value : null;
    }

    private static int? VeniceVideoTryGetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;

        return value.TryGetInt32(out var parsed) ? parsed : null;
    }

    private static string ResolveInputField(string? mediaType)
    {
        var normalized = mediaType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.StartsWith("video/", StringComparison.Ordinal))
            return "video_url";
        if (normalized.StartsWith("audio/", StringComparison.Ordinal))
            return "audio_url";

        return "image_url";
    }

    private static string NormalizeVideoInput(VideoFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            return file.Data;

        if (file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }

        return file.Data.ToDataUrl(file.MediaType);
    }

    private static string NormalizeDuration(int durationSeconds, List<object> warnings)
    {
        return durationSeconds switch
        {
            5 => "5s",
            10 => "10s",
            _ => NormalizeDurationWithClamp(durationSeconds, warnings)
        };
    }

    private static string NormalizeDurationWithClamp(int durationSeconds, List<object> warnings)
    {
        var clamped = durationSeconds <= 5 ? 5 : 10;
        warnings.Add(new
        {
            type = "clamped",
            feature = "duration",
            details = "Venice video duration supports only 5s or 10s. Value was clamped."
        });

        return $"{clamped}s";
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";
        if (url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";
        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";

        return null;
    }
}
