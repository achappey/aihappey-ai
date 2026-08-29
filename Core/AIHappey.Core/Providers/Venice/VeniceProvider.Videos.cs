using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.Venice;

public partial class VeniceProvider
{
    private const string VeniceVideoOperationTokenPrefix = "vnv1_";

    private sealed record VeniceVideoOperationData(
        string QueueId,
        string Model,
        string? DownloadUrl);

    private sealed record VeniceRetrievePollResult(
        bool IsCompleted,
        byte[]? VideoBytes,
        string? MediaType,
        JsonElement? JsonBody);

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        ApplyAuthHeader();
        var submittedAt = DateTime.UtcNow;
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
        var downloadUrl = TryGetString(queueRoot, "download_url");
        return new VideoOperationStartResult
        {
            Operation = EncodeVeniceVideoOperation(queueId, queuedModel, downloadUrl),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                queueId,
                model = queuedModel,
                status = "QUEUED",
                queue = queueRoot
            }),
            Response = new()
            {
                Timestamp = submittedAt,
                ModelId = queuedModel.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var operationData = DecodeVeniceVideoOperation(operation);
        ApplyAuthHeader();

        var retrievePayload = BuildRetrievePayload(operationData.Model, operationData.QueueId);
        var retrieveResult = await RetrieveVideoAsync(retrievePayload, cancellationToken);
        var status = retrieveResult.JsonBody is { } body
            ? TryGetString(body, "status") ?? (retrieveResult.IsCompleted ? "COMPLETED" : "UNKNOWN")
            : "COMPLETED";
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (!retrieveResult.IsCompleted)
        {
            var pendingMetadata = CreateVeniceStatusMetadata(operationData, status, retrieveResult.JsonBody);
            if (IsFailedVeniceVideoStatus(status))
            {
                return new VideoOperationErrorResult
                {
                    Error = ReadVeniceVideoError(retrieveResult.JsonBody)
                        ?? $"Venice video generation failed with status '{status}' (queue_id={operationData.QueueId}).",
                    ProviderMetadata = pendingMetadata,
                    Response = response
                };
            }

            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = pendingMetadata,
                Response = response
            };
        }

        byte[] videoBytes;
        string mediaType;
        try
        {
            (videoBytes, mediaType) = retrieveResult.VideoBytes is { } bytes
                ? (bytes, retrieveResult.MediaType ?? "video/mp4")
                : await DownloadVeniceVideoAsync(operationData.DownloadUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new VideoOperationErrorResult
            {
                Error = ex.Message,
                ProviderMetadata = CreateVeniceStatusMetadata(operationData, status, retrieveResult.JsonBody),
                Response = response
            };
        }

        var (cleanupSucceeded, cleanupResponse, cleanupError) = await CompleteVeniceVideoBestEffortAsync(
            operationData.Model,
            operationData.QueueId,
            cancellationToken);
        var completedMetadata = CreateVeniceStatusMetadata(
            operationData,
            status,
            retrieveResult.JsonBody,
            cleanupSucceeded,
            cleanupResponse,
            cleanupError);

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = mediaType,
                Data = Convert.ToBase64String(videoBytes)
            }],
            Warnings = [],
            ProviderMetadata = completedMetadata,
            Response = response
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

    private async Task<VeniceRetrievePollResult> RetrieveVideoAsync(JsonObject payload, CancellationToken cancellationToken)
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

        if (IsFailedVeniceVideoStatus(status))
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

        if (string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            return new VeniceRetrievePollResult(true, null, null, root);

        throw new InvalidOperationException($"Venice retrieve returned unexpected payload: {raw}");
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadVeniceVideoAsync(
        string? downloadUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("Venice video retrieval completed without video bytes or a download_url.");

        using var response = await _client.GetAsync(downloadUrl, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Venice video download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return (bytes, response.Content.Headers.ContentType?.MediaType
            ?? GuessVideoMediaType(downloadUrl)
            ?? "video/mp4");
    }

    private async Task<(bool Succeeded, JsonElement? Response, string? Error)> CompleteVeniceVideoBestEffortAsync(
        string model,
        string queueId,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = new JsonObject
            {
                ["model"] = model,
                ["queue_id"] = queueId
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/video/complete")
            {
                Content = new StringContent(payload.ToJsonString(JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
            };
            using var response = await _client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (false, TryParseVeniceJson(raw), $"Venice video complete request failed ({(int)response.StatusCode}): {raw}");

            return (true, TryParseVeniceJson(raw), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private Dictionary<string, JsonElement> CreateVeniceStatusMetadata(
        VeniceVideoOperationData operation,
        string status,
        JsonElement? retrieveResponse,
        bool? cleanupSucceeded = null,
        JsonElement? cleanupResponse = null,
        string? cleanupError = null)
        => GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            queueId = operation.QueueId,
            model = operation.Model,
            status,
            retrieve = retrieveResponse,
            cleanup = cleanupSucceeded is null ? null : new
            {
                attempted = true,
                success = cleanupSucceeded,
                response = cleanupResponse,
                error = cleanupError
            }
        });

    private static string EncodeVeniceVideoOperation(string queueId, string model, string? downloadUrl)
    {
        var json = JsonSerializer.Serialize(new VeniceVideoOperationData(queueId, model, downloadUrl), JsonSerializerOptions.Web);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return VeniceVideoOperationTokenPrefix + base64Url;
    }

    private static VeniceVideoOperationData DecodeVeniceVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation)
            || !operation.StartsWith(VeniceVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A valid Venice video operation token is required.", nameof(operation));

        var base64Url = operation[VeniceVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<VeniceVideoOperationData>(json, JsonSerializerOptions.Web);
            if (data is null || string.IsNullOrWhiteSpace(data.QueueId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The Venice video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The Venice video operation token is invalid.", nameof(operation), ex);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The Venice video operation token is invalid.", nameof(operation), ex);
        }
    }

    private static bool IsFailedVeniceVideoStatus(string? status)
        => string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "ERROR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "CANCELED", StringComparison.OrdinalIgnoreCase);

    private static string? ReadVeniceVideoError(JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } value)
            return null;

        return TryGetString(value, "error")
            ?? TryGetString(value, "message")
            ?? TryGetString(value, "detail");
    }

    private static JsonElement? TryParseVeniceJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
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

    private static JsonObject BuildRetrievePayload(string model, string queueId)
    {
        return new JsonObject
        {
            ["model"] = model,
            ["queue_id"] = queueId,
            ["delete_media_on_completion"] = false
        };
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
        payload.Remove("delete_media_on_completion");
        payload.Remove("poll_interval_seconds");
        payload.Remove("poll_timeout_minutes");
        payload.Remove("poll_max_attempts");
        return payload;
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
