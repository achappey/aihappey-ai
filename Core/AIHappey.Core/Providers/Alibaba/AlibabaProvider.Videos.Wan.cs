using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Alibaba;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider
{
    private static readonly JsonSerializerOptions WanVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string DashScopeVideoPath = "/api/v1/services/aigc/video-generation/video-synthesis";

    private async Task<VideoResponse> WanVideoRequest(
        VideoRequest request,
        AlibabaVideoProviderMetadata? providerMetadata,
        string modelName,
        List<object> warnings,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // Singapore/intl only for now.
        var baseUrl = DefaultDashScopeBaseUrl;

        if (request.Image is not null && request.Image.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Wan video generation only supports base64 or data URLs for images.");

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        var wan = providerMetadata?.Wan;

        var payload = BuildWanVideoPayload(request, wan, modelName, warnings);
        var json = JsonSerializer.Serialize(payload, WanVideoJsonOptions);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl}{DashScopeVideoPath}"))
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        createReq.Headers.Add("X-DashScope-Async", "enable");

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"Wan video create failed ({(int)createResp.StatusCode})"
                : $"Wan video create failed ({(int)createResp.StatusCode}): {createRaw}");
        }

        using var createDoc = JsonDocument.Parse(createRaw);
        var taskId = TryGetTaskId(createDoc.RootElement);
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Wan video generation returned no task_id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async token =>
            {
                using var pollReq = new HttpRequestMessage(HttpMethod.Get, new Uri($"{baseUrl}/api/v1/tasks/{taskId}"));
                using var pollResp = await _client.SendAsync(pollReq, token);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(token);
                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Wan video poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return (root: pollDoc.RootElement.Clone(), raw: pollRaw);
            },
            result =>
            {
                var status = TryGetTaskStatus(result.root);
                return string.Equals(status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "CANCELED", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "UNKNOWN", StringComparison.OrdinalIgnoreCase);
            },
            interval: TimeSpan.FromSeconds(15),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var finalStatus = TryGetTaskStatus(completed.root);
        if (!string.Equals(finalStatus, "SUCCEEDED", StringComparison.OrdinalIgnoreCase))
        {
            var error = TryGetErrorMessage(completed.root) ?? "Unknown error";
            throw new InvalidOperationException($"Wan video generation failed: {error}");
        }

        var videoUrl = TryGetVideoUrl(completed.root);
        if (string.IsNullOrWhiteSpace(videoUrl))
            throw new InvalidOperationException("Wan video result contained no video_url.");

        var videoBytes = await _client.GetByteArrayAsync(videoUrl, cancellationToken);
        var mediaType = GuessVideoMediaType(videoUrl) ?? "video/mp4";

        Dictionary<string, JsonElement>? providerMeta = null;
        try
        {
            var meta = new Dictionary<string, JsonElement>
            {
                ["create"] = createDoc.RootElement.Clone(),
                ["poll"] = completed.root.Clone()
            };

            providerMeta = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(meta, JsonSerializerOptions.Web)
            };
        }
        catch
        {
            // best-effort only
        }

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
            ProviderMetadata = providerMeta,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = createDoc.RootElement.Clone()
            }
        };
    }

    private static Dictionary<string, object?> BuildWanVideoPayload(
        VideoRequest request,
        AlibabaWanVideoOptions? wan,
        string modelName,
        List<object> warnings)
    {
        var input = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            input["prompt"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });

        if (request.Image is not null)
        {
            var imageData = request.Image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? request.Image.Data
                : Common.Extensions.ImageExtensions.ToDataUrl(request.Image.Data, request.Image.MediaType);

            input["img_url"] = imageData;
        }

        if (!string.IsNullOrWhiteSpace(wan?.NegativePrompt))
            input["negative_prompt"] = wan.NegativePrompt;

        var parameters = new Dictionary<string, object?>
        {
            ["prompt_extend"] = wan?.PromptExtend,
            ["watermark"] = wan?.Watermark,
            ["shot_type"] = wan?.ShotType
        };

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            parameters["resolution"] = request.Resolution;

        if (request.Duration is not null)
            parameters["duration"] = request.Duration;

        if (request.Seed is not null)
            parameters["seed"] = request.Seed;

        return new Dictionary<string, object?>
        {
            ["model"] = modelName,
            ["input"] = input,
            ["parameters"] = parameters
        };
    }

    private static string? TryGetTaskId(JsonElement root)
    {
        if (root.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("task_id", out var taskId)
            && taskId.ValueKind == JsonValueKind.String)
        {
            return taskId.GetString();
        }

        return null;
    }

    private static string? TryGetTaskStatus(JsonElement root)
    {
        if (root.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("task_status", out var status)
            && status.ValueKind == JsonValueKind.String)
        {
            return status.GetString();
        }

        return null;
    }

    private static string? TryGetVideoUrl(JsonElement root)
    {
        if (root.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("video_url", out var video)
            && video.ValueKind == JsonValueKind.String)
        {
            return video.GetString();
        }

        return null;
    }

    private static string? TryGetErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String)
        {
            return message.GetString();
        }

        if (root.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("message", out var outputMessage)
            && outputMessage.ValueKind == JsonValueKind.String)
        {
            return outputMessage.GetString();
        }

        return null;
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
