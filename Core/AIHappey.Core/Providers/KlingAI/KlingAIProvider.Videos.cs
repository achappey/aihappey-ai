using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.KlingAI;

public partial class KlingAIProvider : IModelProvider
{
    private static readonly JsonSerializerOptions VideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt) && request.Image is null)
            throw new ArgumentException("Prompt or image is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (request.Image is not null && request.Image.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("KlingAI video generation only supports base64 or data URLs for images.");

        var endpoint = ResolveVideoEndpoint(request);
        var payload = BuildVideoPayload(request, warnings);

        var json = JsonSerializer.Serialize(payload, VideoJson);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"KlingAI video create failed ({(int)createResp.StatusCode})"
                : $"KlingAI video create failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement;

        EnsureKlingOk(createRoot, "video_create");
        var taskId = ExtractVideoTaskId(createRoot);
        var final = await PollVideoTaskAsync(endpoint, taskId, cancellationToken);
        var (videoBytes, mediaType) = await ExtractVideoAsync(final, cancellationToken);

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
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = final.Clone()
            }
        };
    }

    private static string ResolveVideoEndpoint(VideoRequest request)
    {
        var model = NormalizeVideoModelName(request.Model);
        if (string.Equals(model, "avatar", StringComparison.OrdinalIgnoreCase))
            return "v1/videos/avatar/image2video";

        if (string.Equals(model, "kling-video-o1", StringComparison.OrdinalIgnoreCase))
            return "v1/videos/omni-video";

        return request.Image is not null ? "v1/videos/image2video" : "v1/videos/text2video";
    }

    private static Dictionary<string, object?> BuildVideoPayload(VideoRequest request, List<object> warnings)
    {
        var model = NormalizeVideoModelName(request.Model);
        var payload = new Dictionary<string, object?>
        {
            ["model_name"] = model
        };

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        if (request.Image is not null)
        {
            var imageData = request.Image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? request.Image.Data.RemoveDataUrlPrefix()
                : request.Image.Data;

            if (string.Equals(model, "kling-video-o1", StringComparison.OrdinalIgnoreCase))
            {
                payload["image_list"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["image_url"] = imageData
                    }
                };
            }
            else
            {
                payload["image"] = imageData;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Resolution) && !string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new { type = "compatibility", feature = "resolution", details = "Resolution provided with aspect_ratio; KlingAI may prefer aspect_ratio." });
        }

        return payload;
    }

    private async Task<JsonElement> PollVideoTaskAsync(string endpoint, string taskId, CancellationToken cancellationToken)
    {
        var pollEndpoint = endpoint.TrimEnd('/') + "/" + taskId;

        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: async ct =>
            {
                using var pollResp = await _client.GetAsync(pollEndpoint, ct);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(ct);
                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"KlingAI video poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return pollDoc.RootElement.Clone();
            },
            isTerminal: r =>
            {
                var status = GetVideoTaskStatus(r);
                return status is "succeed" or "failed";
            },
            interval: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var status = GetVideoTaskStatus(final);
        if (status == "failed")
        {
            var msg = TryGetVideoStatusMessage(final) ?? "KlingAI video task failed.";
            throw new InvalidOperationException(msg);
        }

        return final;
    }

    private async Task<(byte[] Bytes, string MediaType)> ExtractVideoAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("KlingAI poll response missing data object.");

        if (!data.TryGetProperty("task_result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("KlingAI poll response missing task_result.");

        if (!result.TryGetProperty("videos", out var videosEl) || videosEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("KlingAI poll response missing videos array.");

        foreach (var video in videosEl.EnumerateArray())
        {
            if (!video.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;

            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            using var videoResp = await _client.GetAsync(url, cancellationToken);
            var bytes = await videoResp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!videoResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Failed to download KlingAI video: {videoResp.StatusCode}");

            var mediaType = videoResp.Content.Headers.ContentType?.MediaType
                ?? GuessVideoMediaType(url)
                ?? "video/mp4";

            return (bytes, mediaType);
        }

        throw new InvalidOperationException("KlingAI returned no videos.");
    }

    private static string ExtractVideoTaskId(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("task_id", out var taskIdEl) && taskIdEl.ValueKind == JsonValueKind.String)
            {
                var taskId = taskIdEl.GetString();
                if (!string.IsNullOrWhiteSpace(taskId))
                    return taskId;
            }
        }

        throw new InvalidOperationException("No task_id returned from KlingAI API.");
    }

    private static string? GetVideoTaskStatus(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        if (data.TryGetProperty("task_status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
            return statusEl.GetString();

        return null;
    }

    private static string? TryGetVideoStatusMessage(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        if (data.TryGetProperty("task_status_msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
            return msgEl.GetString();

        return null;
    }

    private static void EnsureKlingOk(JsonElement root, string operation)
    {
        if (!root.TryGetProperty("code", out var codeEl) || codeEl.ValueKind != JsonValueKind.Number)
            return;

        if (codeEl.GetInt32() == 0)
            return;

        var message = root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
            ? msgEl.GetString()
            : "KlingAI request failed";

        throw new InvalidOperationException($"KlingAI {operation} failed: {message}");
    }

    private static string NormalizeVideoModelName(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return model;

        var trimmed = model.Trim();
        var slash = trimmed.IndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
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
}
