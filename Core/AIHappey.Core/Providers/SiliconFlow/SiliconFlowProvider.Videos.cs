using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SiliconFlow;

public partial class SiliconFlowProvider
{
    private sealed class SiliconFlowVideoProviderOptions
    {
        [JsonPropertyName("negativePrompt")]
        public string? NegativePrompt { get; set; }

        [JsonPropertyName("imageSize")]
        public string? ImageSize { get; set; }
    }

    private sealed record SiliconFlowVideoStatus(string Status, JsonElement Root);

    private static readonly JsonSerializerOptions VideoJsonOptions = new(JsonSerializerDefaults.Web)
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
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        var providerOptions = GetVideoProviderOptions(request, GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["image_size"] = providerOptions?.ImageSize ?? request.Resolution ?? "1280x720"
        };

        if (!string.IsNullOrWhiteSpace(providerOptions?.NegativePrompt))
            payload["negative_prompt"] = providerOptions.NegativePrompt;

        if (request.Seed is not null)
            payload["seed"] = request.Seed;

        if (request.Image is not null)
            payload["image"] = ToSiliconFlowImageInput(request.Image);

        var submitJson = JsonSerializer.Serialize(payload, VideoJsonOptions);
        using var submitReq = new HttpRequestMessage(HttpMethod.Post, "v1/video/submit")
        {
            Content = new StringContent(submitJson, Encoding.UTF8, "application/json")
        };

        using var submitResp = await _client.SendAsync(submitReq, cancellationToken);
        var submitRaw = await submitResp.Content.ReadAsStringAsync(cancellationToken);
        if (!submitResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"SiliconFlow video submit failed ({(int)submitResp.StatusCode}): {submitRaw}");

        using var submitDoc = JsonDocument.Parse(submitRaw);
        var requestId = submitDoc.RootElement.TryGetProperty("requestId", out var requestIdEl)
            && requestIdEl.ValueKind == JsonValueKind.String
            ? requestIdEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(requestId))
            throw new InvalidOperationException("SiliconFlow video submit did not return requestId.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollVideoStatusAsync(requestId, ct),
            isTerminal: state => IsTerminalStatus(state.Status),
            interval: TimeSpan.FromSeconds(5),
            timeout: null,
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (!string.Equals(completed.Status, "Succeed", StringComparison.OrdinalIgnoreCase))
        {
            var reason = TryGetFailReason(completed.Root);
            throw new InvalidOperationException($"SiliconFlow video generation failed with status '{completed.Status}': {reason}");
        }

        var videoUrls = TryGetVideoUrls(completed.Root);
        if (videoUrls.Count == 0)
            throw new InvalidOperationException("SiliconFlow video status succeeded but no video URLs were returned.");

        var downloadClient = _factory.CreateClient();
        List<VideoResponseFile> videos = [];
        foreach (var videoUrl in videoUrls)
        {
            using var mediaResp = await downloadClient.GetAsync(videoUrl, cancellationToken);
            var bytes = await mediaResp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!mediaResp.IsSuccessStatusCode)
            {
                var text = Encoding.UTF8.GetString(bytes);
                throw new InvalidOperationException($"SiliconFlow video download failed ({(int)mediaResp.StatusCode}): {text}");
            }

            var mediaType = mediaResp.Content.Headers.ContentType?.MediaType
                ?? GuessVideoMediaType(videoUrl)
                ?? "video/mp4";

            videos.Add(new VideoResponseFile
            {
                MediaType = mediaType,
                Data = Convert.ToBase64String(bytes)
            });
        }

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                {
                    ["submit"] = submitDoc.RootElement.Clone(),
                    ["status"] = completed.Root.Clone()
                }, JsonSerializerOptions.Web)
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = completed.Root.Clone()
            }
        };
    }

    private async Task<SiliconFlowVideoStatus> PollVideoStatusAsync(string requestId, CancellationToken cancellationToken)
    {
        var statusPayload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["requestId"] = requestId
        }, VideoJsonOptions);

        using var statusReq = new HttpRequestMessage(HttpMethod.Post, "v1/video/status")
        {
            Content = new StringContent(statusPayload, Encoding.UTF8, "application/json")
        };

        using var statusResp = await _client.SendAsync(statusReq, cancellationToken);
        var statusRaw = await statusResp.Content.ReadAsStringAsync(cancellationToken);
        if (!statusResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"SiliconFlow video status failed ({(int)statusResp.StatusCode}): {statusRaw}");

        using var statusDoc = JsonDocument.Parse(statusRaw);
        var root = statusDoc.RootElement.Clone();
        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString() ?? "Unknown"
            : "Unknown";

        return new SiliconFlowVideoStatus(status, root);
    }

    private static SiliconFlowVideoProviderOptions? GetVideoProviderOptions(VideoRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return null;

        if (!request.ProviderOptions.TryGetValue(providerId, out var providerJson))
            return null;

        if (providerJson.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        try
        {
            return JsonSerializer.Deserialize<SiliconFlowVideoProviderOptions>(providerJson.GetRawText(), JsonSerializerOptions.Web);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTerminalStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return string.Equals(status, "Succeed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string TryGetFailReason(JsonElement root)
    {
        if (root.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String)
            return reasonEl.GetString() ?? "Unknown error";

        if (root.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String)
            return messageEl.GetString() ?? "Unknown error";

        return "Unknown error";
    }

    private static List<string> TryGetVideoUrls(JsonElement root)
    {
        List<string> urls = [];

        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
            return urls;

        if (!results.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array)
            return urls;

        foreach (var video in videos.EnumerateArray())
        {
            if (!video.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;

            var url = urlEl.GetString();
            if (!string.IsNullOrWhiteSpace(url))
                urls.Add(url);
        }

        return urls;
    }

    private static string ToSiliconFlowImageInput(VideoFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Image data is required.", nameof(file));

        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;

        if (file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }

        if (!string.IsNullOrWhiteSpace(file.MediaType))
            return $"data:{file.MediaType};base64,{file.Data}";

        return file.Data;
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";

        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }
}
