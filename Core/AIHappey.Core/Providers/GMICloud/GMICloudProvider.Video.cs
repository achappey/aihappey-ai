using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.GMICloud;

public partial class GMICloudProvider
{
    private static readonly JsonSerializerOptions GMICloudVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record GMICloudVideoFetchResult(string Status, string Raw, JsonElement Root);

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
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

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => FetchGMICloudVideoRequestAsync(requestId, ct),
            isTerminal: r => IsTerminalGMICloudVideoStatus(r.Status),
            interval: TimeSpan.FromSeconds(3),
            timeout: TimeSpan.FromMinutes(15),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (IsFailedGMICloudVideoStatus(completed.Status))
            throw new InvalidOperationException($"GMICloud video generation failed with status '{completed.Status}' (request_id={requestId}): {completed.Raw}");

        var videoUrl = TryGetGMICloudVideoUrl(completed.Root);
        if (string.IsNullOrWhiteSpace(videoUrl))
            throw new InvalidOperationException($"GMICloud video generation completed but returned no video_url (request_id={requestId}).");

        using var videoResp = await _client.GetAsync(videoUrl, cancellationToken);
        var videoBytes = await videoResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResp.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(videoBytes);
            throw new InvalidOperationException($"GMICloud video download failed ({(int)videoResp.StatusCode}): {error}");
        }

        var mediaType = videoResp.Content.Headers.ContentType?.MediaType
            ?? GuessGMICloudVideoMediaType(videoUrl)
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
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                {
                    ["create"] = createRoot,
                    ["result"] = completed.Root.Clone()
                }, JsonSerializerOptions.Web)
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new Dictionary<string, object?>
                {
                    ["requestId"] = requestId,
                    ["status"] = completed.Status,
                    ["create"] = createRoot,
                    ["result"] = completed.Root.Clone()
                }
            }
        };
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

    private async Task<GMICloudVideoFetchResult> FetchGMICloudVideoRequestAsync(string requestId, CancellationToken cancellationToken)
    {
        using var fetchReq = new HttpRequestMessage(HttpMethod.Get, $"https://console.gmicloud.ai/api/v1/ie/requestqueue/apikey/requests/{Uri.EscapeDataString(requestId)}");
        using var fetchResp = await _client.SendAsync(fetchReq, cancellationToken);
        var fetchRaw = await fetchResp.Content.ReadAsStringAsync(cancellationToken);

        if (!fetchResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GMICloud video poll failed ({(int)fetchResp.StatusCode}): {fetchRaw}");

        using var fetchDoc = JsonDocument.Parse(fetchRaw);
        var root = fetchDoc.RootElement.Clone();
        var status = TryGetString(root, "status") ?? "unknown";

        return new GMICloudVideoFetchResult(status, fetchRaw, root);
    }

    private static bool IsTerminalGMICloudVideoStatus(string? status)
        => status is not null
            && (status.Equals("success", StringComparison.OrdinalIgnoreCase)
                || status.Equals("finished", StringComparison.OrdinalIgnoreCase)
                || status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("error", StringComparison.OrdinalIgnoreCase)
                || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
                || status.Equals("canceled", StringComparison.OrdinalIgnoreCase));

    private static bool IsFailedGMICloudVideoStatus(string? status)
        => string.IsNullOrWhiteSpace(status)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase);

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
