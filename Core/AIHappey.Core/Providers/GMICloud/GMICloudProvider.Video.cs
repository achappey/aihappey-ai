using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.GMICloud;

public partial class GMICloudProvider
{
    private const string GMICloudVideoOperationTokenPrefix = "gmiv1_";

    private static readonly JsonSerializerOptions GMICloudVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record GMICloudVideoOperationData(string RequestId, string Model);

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt) && request.Image is null)
            throw new ArgumentException("Prompt is required when image is not provided.", nameof(request));

        if (request.Image is not null && !request.Image.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("GMICloud video image input must be an image/* media type.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            warnings.Add(new { type = "unsupported", feature = "resolution", details = "GMI video requestqueue models expose model-specific resolution settings via providerOptions.gmicloud.payload when available." });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        var payload = BuildGMICloudVideoPayload(request);
        var createPayload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["payload"] = payload
        };

        var createJson = JsonSerializer.Serialize(createPayload, GMICloudVideoJsonOptions);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "https://console.gmicloud.ai/api/v1/ie/requestqueue/apikey/requests")
        {
            Content = new StringContent(createJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GMICloud video create failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();

        var requestId = TryGetString(createRoot, "request_id")
            ?? throw new InvalidOperationException("GMICloud video create response missing request_id.");

        var status = TryGetString(createRoot, "status") ?? "unknown";
        return new VideoOperationStartResult
        {
            Operation = EncodeGMICloudVideoOperation(requestId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                requestId,
                status,
                request = createRoot
            }),
            Response = new()
            {
                Timestamp = now,
                Headers = createResp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeGMICloudVideoOperation(operation);
        ApplyAuthHeader();

        using var pollReq = new HttpRequestMessage(HttpMethod.Get,
            $"https://console.gmicloud.ai/api/v1/ie/requestqueue/apikey/requests/{Uri.EscapeDataString(operationData.RequestId)}");
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);

        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GMICloud video poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var status = TryGetString(root, "status") ?? "unknown";
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            requestId = operationData.RequestId,
            status,
            request = root
        });
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = pollResp.GetHeaders(),
            // The submitted model is authoritative. GMICloud polling may omit
            // the model or report a routed model rather than the caller's ID.
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (IsFailedGMICloudVideoStatus(status))
            return new VideoOperationErrorResult
            {
                Error = $"GMICloud video generation failed with status '{status}' (request_id={operationData.RequestId}): {pollRaw}",
                ProviderMetadata = metadata,
                Response = response
            };

        if (!IsSuccessfulGMICloudVideoStatus(status))
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = metadata,
                Response = response
            };

        var videoUrl = TryGetGMICloudVideoUrl(root);
        if (string.IsNullOrWhiteSpace(videoUrl))
            return new VideoOperationErrorResult
            {
                Error = $"GMICloud video generation completed but returned no video_url (request_id={operationData.RequestId}).",
                ProviderMetadata = metadata,
                Response = response
            };

        using var videoResp = await _client.GetAsync(videoUrl, cancellationToken);
        var videoBytes = await videoResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GMICloud video download failed ({(int)videoResp.StatusCode}): {Encoding.UTF8.GetString(videoBytes)}");

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = videoResp.Content.Headers.ContentType?.MediaType
                    ?? GuessGMICloudVideoMediaType(videoUrl)
                    ?? "video/mp4",
                Data = Convert.ToBase64String(videoBytes)
            }],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static string EncodeGMICloudVideoOperation(string requestId, string model)
    {
        var json = JsonSerializer.Serialize(new GMICloudVideoOperationData(requestId, model), GMICloudVideoJsonOptions);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return GMICloudVideoOperationTokenPrefix + base64Url;
    }

    private static GMICloudVideoOperationData DecodeGMICloudVideoOperation(string operation)
    {
        if (!operation.StartsWith(GMICloudVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The GMICloud video operation token is invalid.", nameof(operation));

        var base64Url = operation[GMICloudVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<GMICloudVideoOperationData>(json, GMICloudVideoJsonOptions);
            if (data is null || string.IsNullOrWhiteSpace(data.RequestId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The GMICloud video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The GMICloud video operation token is invalid.", nameof(operation), ex);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The GMICloud video operation token is invalid.", nameof(operation), ex);
        }
    }

    private static Dictionary<string, object?> BuildGMICloudVideoPayload(VideoRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = string.IsNullOrWhiteSpace(request.Prompt) ? null : request.Prompt,
            ["durationSeconds"] = request.Duration?.ToString(),
            ["aspectRatio"] = string.IsNullOrWhiteSpace(request.AspectRatio) ? null : request.AspectRatio,
            ["seed"] = request.Seed
        };

        if (request.Image is not null)
            payload["image"] = request.Image.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || request.Image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? request.Image.Data
                    : request.Image.Data.ToDataUrl(request.Image.MediaType);

        var providerOptions = GetGMICloudVideoProviderOptions(request, "gmicloud")
            ?? GetGMICloudVideoProviderOptions(request, nameof(GMICloud).ToLowerInvariant());

        if (providerOptions?.Payload is { ValueKind: JsonValueKind.Object } extraPayload)
        {
            foreach (var property in extraPayload.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static bool IsSuccessfulGMICloudVideoStatus(string? status)
        => status is not null
            && (status.Equals("success", StringComparison.OrdinalIgnoreCase)
                || status.Equals("finished", StringComparison.OrdinalIgnoreCase)
                || status.Equals("completed", StringComparison.OrdinalIgnoreCase));

    private static bool IsFailedGMICloudVideoStatus(string? status)
        => string.IsNullOrWhiteSpace(status)
             || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
             || status.Equals("error", StringComparison.OrdinalIgnoreCase)
             || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
             || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
             || status.Equals("rejected", StringComparison.OrdinalIgnoreCase)
             || status.Equals("expired", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetGMICloudVideoUrl(JsonElement root)
    {
        if (TryGetString(root, "outcome", "video_url") is { } outcomeVideoUrl)
            return outcomeVideoUrl;

        if (TryGetString(root, "outcome", "videoUrl") is { } outcomeVideoUrlCamel)
            return outcomeVideoUrlCamel;

        if (TryGetString(root, "video_url") is { } videoUrl)
            return videoUrl;

        if (TryGetString(root, "videoUrl") is { } videoUrlCamel)
            return videoUrlCamel;

        return null;
    }

    private static string? TryGetString(JsonElement root, params string[] path)
    {
        var current = root;

        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string? GuessGMICloudVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.Contains(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (url.Contains(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";
        if (url.Contains(".mkv", StringComparison.OrdinalIgnoreCase))
            return "video/x-matroska";
        if (url.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }

    private sealed class GMICloudVideoProviderOptions
    {
        [JsonPropertyName("payload")]
        public JsonElement? Payload { get; set; }
    }

    private static GMICloudVideoProviderOptions? GetGMICloudVideoProviderOptions(VideoRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return default;

        if (!request.ProviderOptions.TryGetValue(providerId, out var element))
            return default;

        try
        {
            return JsonSerializer.Deserialize<GMICloudVideoProviderOptions>(element.GetRawText(), JsonSerializerOptions.Web);
        }
        catch
        {
            return default;
        }
    }
}
