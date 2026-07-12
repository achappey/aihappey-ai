using System.Text;
using System.Text.Json;
using System.Net.Mime;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.Thalam;

public partial class ThalamProvider
{
    private sealed record ThalamVideoTaskStatus(string Status, string Raw, JsonElement Root);

    private async Task<VideoResponse> ThalamVideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
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

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.InputReferences?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "inputReferences" });

        if (request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "frameImages" });

        var providerOptions = GetThalamProviderOptions(request.ProviderOptions);
        var payload = BuildThalamVideoPayload(request, providerOptions);
        var json = JsonSerializer.Serialize(payload, ThalamJsonOptions);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"Thalam video generation failed ({(int)createResponse.StatusCode})."
                : $"Thalam video generation failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var createDocument = JsonDocument.Parse(createRaw);
        var createRoot = createDocument.RootElement.Clone();
        var taskId = createRoot.TryGetString("task_id", "taskId", "id");

        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Thalam video generation returned no task_id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: token => FetchThalamVideoTaskAsync(taskId, token),
            isTerminal: result => IsThalamVideoTerminalStatus(result.Status),
            interval: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromMinutes(15),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (!IsThalamVideoSuccessStatus(completed.Status))
        {
            var reason = TryGetThalamVideoFailureReason(completed.Root);
            throw new InvalidOperationException($"Thalam video task failed with status '{completed.Status}' (task_id={taskId}). {reason}".Trim());
        }

        var videoUrl = TryGetThalamVideoUrl(completed.Root);
        if (string.IsNullOrWhiteSpace(videoUrl))
            throw new InvalidOperationException($"Thalam video task completed but returned no video_url (task_id={taskId}).");

        var downloaded = await DownloadThalamMediaAsync(videoUrl, "video/mp4", cancellationToken);

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = NormalizeThalamVideoMediaType(downloaded.MediaType, videoUrl),
                    Data = Convert.ToBase64String(downloaded.Bytes)
                }
            ],
            Warnings = warnings,
            ProviderMetadata = CreateThalamProviderMetadata(new
            {
                endpoint = "v1/videos/generations",
                pollEndpoint = "v1/videos/tasks/{task_id}",
                taskId,
                payload,
                create = createRoot,
                poll = completed.Root,
                videoUrl
            }),
            Response = new()
            {
                Timestamp = now,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static Dictionary<string, object?> BuildThalamVideoPayload(VideoRequest request, JsonElement providerOptions)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["duration"] = request.Duration,
            ["resolution"] = request.Resolution,
            ["aspect_ratio"] = request.AspectRatio
        };

        if (request.Image is not null)
            payload["image_url"] = request.Image.Data;

        MergeThalamProviderOptions(payload, providerOptions);
        return payload;
    }

    private async Task<ThalamVideoTaskStatus> FetchThalamVideoTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        using var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"v1/videos/tasks/{Uri.EscapeDataString(taskId)}");
        using var pollResponse = await _client.SendAsync(pollRequest, cancellationToken);
        var raw = await pollResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!pollResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Thalam video status failed ({(int)pollResponse.StatusCode}): {raw}");

        using var pollDocument = JsonDocument.Parse(raw);
        var root = pollDocument.RootElement.Clone();
        var status = root.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.Object
            ? task.TryGetString("status") ?? "unknown"
            : root.TryGetString("status") ?? "unknown";

        return new ThalamVideoTaskStatus(status, raw, root);
    }

    private static bool IsThalamVideoTerminalStatus(string? status)
        => string.Equals(status, "TASK_STATUS_SUCCEED", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "TASK_STATUS_FAILED", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);

    private static bool IsThalamVideoSuccessStatus(string? status)
        => string.Equals(status, "TASK_STATUS_SUCCEED", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetThalamVideoUrl(JsonElement root)
    {
        if (root.TryGetProperty("videos", out var videos) && videos.ValueKind == JsonValueKind.Array)
        {
            foreach (var video in videos.EnumerateArray())
            {
                var url = video.TryGetString("video_url", "videoUrl", "url");
                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }
        }

        return root.TryGetString("video_url", "videoUrl", "url");
    }

    private static string TryGetThalamVideoFailureReason(JsonElement root)
    {
        if (root.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.Object)
        {
            var reason = task.TryGetString("reason", "message", "error");
            if (!string.IsNullOrWhiteSpace(reason))
                return reason;
        }

        return root.TryGetString("reason", "message", "error") ?? string.Empty;
    }
}
