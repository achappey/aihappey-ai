using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AICC;

public partial class AICCProvider
{
    private static readonly JsonSerializerOptions AiccVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<VideoResponse> VideoRequestAICC(VideoRequest request, CancellationToken cancellationToken = default)
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

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        if (request.Image is not null)
            warnings.Add(new { type = "unsupported", feature = "image" });

        var payload = BuildCreateVideoPayload(request);
        var json = JsonSerializer.Serialize(payload, AiccVideoJsonOptions);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v1/video/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AICC video create failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var taskId = TryGetString(createDoc.RootElement, "id");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("AICC video create response contained no task id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async token =>
            {
                using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"v1/video/generations/{taskId}");
                using var pollResp = await _client.SendAsync(pollReq, token);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(token);

                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"AICC video status failed ({(int)pollResp.StatusCode}): {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return pollDoc.RootElement.Clone();
            },
            root => IsTerminalStatus(TryGetString(root, "status")),
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var finalStatus = TryGetString(completed, "status");
        if (!IsSuccessStatus(finalStatus))
            throw new InvalidOperationException($"AICC video task failed with status '{finalStatus ?? "unknown"}'.");

        var videoBytes = await DownloadVideoAsync(taskId, completed, cancellationToken);
        var mediaType = ResolveMediaType(completed) ?? "video/mp4";

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                family = "video-task",
                create = createDoc.RootElement.Clone(),
                poll = completed
            }, JsonSerializerOptions.Web)
        };

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
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = completed
            }
        };
    }

    private Dictionary<string, object?> BuildCreateVideoPayload(VideoRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = new Dictionary<string, object?>
            {
                ["prompt"] = request.Prompt
            },
            ["parameters"] = new Dictionary<string, object?>()
        };

        var parameters = (Dictionary<string, object?>)payload["parameters"]!;

        if (request.Duration is not null)
            parameters["duration"] = request.Duration;

        var normalizedSize = NormalizeResolutionAsAiccSize(request.Resolution);
        if (!string.IsNullOrWhiteSpace(normalizedSize))
            parameters["size"] = normalizedSize;

        if (request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue(GetIdentifier(), out var providerOptions)
            && providerOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerOptions.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private async Task<byte[]> DownloadVideoAsync(string taskId, JsonElement finalStatusBody, CancellationToken cancellationToken)
    {
        var videoUrl = TryGetVideoUrl(finalStatusBody);
        if (!string.IsNullOrWhiteSpace(videoUrl))
            return await _client.GetByteArrayAsync(videoUrl, cancellationToken);

        using var contentReq = new HttpRequestMessage(HttpMethod.Get, $"v1/videos/{taskId}/content");
        using var contentResp = await _client.SendAsync(contentReq, cancellationToken);

        if (!contentResp.IsSuccessStatusCode)
        {
            var errorRaw = await contentResp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"AICC video content failed ({(int)contentResp.StatusCode}): {errorRaw}");
        }

        return await contentResp.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static string? TryGetVideoUrl(JsonElement root)
    {
        if (TryGetString(root, "url") is { } direct && !string.IsNullOrWhiteSpace(direct))
            return direct;

        if (TryGetString(root, "video_url") is { } videoUrl && !string.IsNullOrWhiteSpace(videoUrl))
            return videoUrl;

        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(metadata, "url") is { } metadataUrl && !string.IsNullOrWhiteSpace(metadataUrl))
                return metadataUrl;

            if (TryGetString(metadata, "video_url") is { } metadataVideoUrl && !string.IsNullOrWhiteSpace(metadataVideoUrl))
                return metadataVideoUrl;
        }

        return null;
    }

    private static string? ResolveMediaType(JsonElement root)
    {
        if (TryGetString(root, "format") is { } format)
        {
            if (string.Equals(format, "mp4", StringComparison.OrdinalIgnoreCase))
                return "video/mp4";

            if (string.Equals(format, "webm", StringComparison.OrdinalIgnoreCase))
                return "video/webm";
        }

        var url = TryGetVideoUrl(root);
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }

    private static bool IsTerminalStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
               || status.Equals("completed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("success", StringComparison.OrdinalIgnoreCase)
               || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("error", StringComparison.OrdinalIgnoreCase)
               || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSuccessStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
               || status.Equals("completed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("success", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeResolutionAsAiccSize(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return null;

        var normalized = resolution.Trim().Replace("x", "*", StringComparison.OrdinalIgnoreCase)
            .Replace(":", "*", StringComparison.OrdinalIgnoreCase);

        var parts = normalized.Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return resolution;

        if (int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _))
            return $"{parts[0]}*{parts[1]}";

        return resolution;
    }

}
