using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.AI302;

public partial class AI302Provider
{
    private static readonly JsonSerializerOptions AI302VideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record AI302VideoFetchResult(string Status, string Raw, JsonElement Root);

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        var metadata = GetVideoProviderMetadata<AI302VideoProviderMetadata>(request, GetIdentifier());

        var payload = BuildVideoPayload(request, metadata);
        var createPath = BuildCreateVideoPath(metadata?.Webhook);
        var createJson = JsonSerializer.Serialize(payload, AI302VideoJsonOptions);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, createPath)
        {
            Content = new StringContent(createJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"302.AI video generation failed ({(int)createResp.StatusCode})."
                : $"302.AI video generation failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement;

        var taskId = createRoot.TryGetProperty("task_id", out var taskEl)
            ? taskEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("302.AI video generation returned no task_id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => FetchVideoTaskAsync(taskId, ct),
            isTerminal: r => IsTerminalStatus(r.Status),
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (IsFailedStatus(completed.Status))
            throw new InvalidOperationException($"302.AI video generation failed with status '{completed.Status}' (task_id={taskId}). Response: {completed.Raw}");

        var videoUrl = TryGetVideoUrl(completed.Root);
        if (string.IsNullOrWhiteSpace(videoUrl))
            throw new InvalidOperationException($"302.AI video task completed but returned no video url (task_id={taskId}).");

        using var videoResp = await _client.GetAsync(videoUrl, cancellationToken);
        var videoBytes = await videoResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"302.AI video download failed ({(int)videoResp.StatusCode}).");

        var mediaType = videoResp.Content.Headers.ContentType?.MediaType
            ?? GuessVideoMediaType(videoUrl)
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
                [GetIdentifier()] = completed.Root.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = createRoot.Clone()
            }
        };
    }

    private static Dictionary<string, object?> BuildVideoPayload(VideoRequest request, AI302VideoProviderMetadata? metadata)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };

        if (request.Image is not null)
            payload["image"] = EnsureImageInput(request.Image.Data, request.Image.MediaType);

        if (metadata?.ImageList?.Count > 0)
        {
            payload["image"] = metadata.ImageList
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.StartsWith("http", StringComparison.OrdinalIgnoreCase) || x.StartsWith("data:image", StringComparison.OrdinalIgnoreCase)
                    ? x
                    : x.ToDataUrl(MediaTypeNames.Image.Png))
                .ToArray();
        }

        if (!string.IsNullOrWhiteSpace(metadata?.EndImage))
            payload["end_image"] = EnsureImageInput(metadata.EndImage, MediaTypeNames.Image.Png);

        if (!string.IsNullOrWhiteSpace(metadata?.Video))
            payload["video"] = metadata.Video;

        if (!string.IsNullOrWhiteSpace(metadata?.NegativePrompt))
            payload["negative_prompt"] = metadata.NegativePrompt;

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        if (request.Fps is not null)
            payload["fps"] = request.Fps.Value.ToString();

        return payload;
    }

    private async Task<AI302VideoFetchResult> FetchVideoTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        using var fetchReq = new HttpRequestMessage(HttpMethod.Get, $"302/v2/video/fetch/{Uri.EscapeDataString(taskId)}");
        using var fetchResp = await _client.SendAsync(fetchReq, cancellationToken);
        var fetchRaw = await fetchResp.Content.ReadAsStringAsync(cancellationToken);
        if (!fetchResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"302.AI video fetch failed ({(int)fetchResp.StatusCode}): {fetchRaw}");

        using var fetchDoc = JsonDocument.Parse(fetchRaw);
        var root = fetchDoc.RootElement.Clone();
        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString() ?? "unknown"
            : "unknown";

        return new AI302VideoFetchResult(status, fetchRaw, root);
    }

    private static bool IsTerminalStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("success", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFailedStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return true;

        return status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetVideoUrl(JsonElement root)
    {
        if (root.TryGetProperty("video_url", out var directUrl)
            && directUrl.ValueKind == JsonValueKind.String)
        {
            var value = directUrl.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        if (root.TryGetProperty("raw_response", out var rawResponse)
            && rawResponse.ValueKind == JsonValueKind.Object
            && rawResponse.TryGetProperty("file", out var file)
            && file.ValueKind == JsonValueKind.Object
            && file.TryGetProperty("download_url", out var downloadUrl)
            && downloadUrl.ValueKind == JsonValueKind.String)
        {
            var value = downloadUrl.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string BuildCreateVideoPath(string? webhook)
    {
        var endpoint = "302/v2/video/create";
        if (string.IsNullOrWhiteSpace(webhook))
            return endpoint;

        return $"{endpoint}?webhook={Uri.EscapeDataString(webhook)}";
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";
        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
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

    private sealed class AI302VideoProviderMetadata
    {
        [JsonPropertyName("webhook")]
        public string? Webhook { get; set; }

        [JsonPropertyName("negative_prompt")]
        public string? NegativePrompt { get; set; }

        [JsonPropertyName("end_image")]
        public string? EndImage { get; set; }

        [JsonPropertyName("video")]
        public string? Video { get; set; }

        [JsonPropertyName("image")]
        public List<string>? ImageList { get; set; }
    }
}

