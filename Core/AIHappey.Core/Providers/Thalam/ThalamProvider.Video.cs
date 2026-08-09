using System.Text;
using System.Text.Json;
using System.Net.Mime;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.Thalam;

public partial class ThalamProvider
{
    private const string ThalamVideoOperationTokenPrefix = "tlv1_";

    private static readonly JsonSerializerOptions ThalamVideoOperationJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record ThalamVideoTaskStatus(string Status, string Raw, JsonElement Root);
    private sealed record ThalamVideoOperationData(string TaskId, string? Model);

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = GetThalamVideoWarnings(request);
        var payload = BuildThalamVideoPayload(request, GetThalamProviderOptions(request.ProviderOptions));
        var json = JsonSerializer.Serialize(payload, ThalamJsonOptions);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var raw = await createResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Thalam video generation failed ({(int)createResponse.StatusCode})."
                : $"Thalam video generation failed ({(int)createResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var taskId = root.TryGetString("task_id", "taskId", "id");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Thalam video generation returned no task_id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeThalamVideoOperation(taskId, request.Model),
            Warnings = warnings,
            ProviderMetadata = CreateThalamProviderMetadata(new
            {
                endpoint = "v1/videos/generations",
                taskId,
                payload,
                response = root
            }),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeThalamVideoOperation(operation);
        ApplyAuthHeader();
        var result = await FetchThalamVideoTaskAsync(operationData.TaskId, cancellationToken);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = string.IsNullOrWhiteSpace(operationData.Model)
                ? GetIdentifier()
                : operationData.Model.ToModelId(GetIdentifier())
        };
        var metadata = CreateThalamProviderMetadata(new
        {
            endpoint = "v1/videos/tasks/{task_id}",
            taskId = operationData.TaskId,
            status = result.Status,
            response = result.Root
        });

        if (!IsThalamVideoTerminalStatus(result.Status))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        if (!IsThalamVideoSuccessStatus(result.Status))
        {
            var reason = TryGetThalamVideoFailureReason(result.Root);
            var detail = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" {reason}";
            return new VideoOperationErrorResult
            {
                Error = $"Thalam video task failed with status '{result.Status}' (task_id={operationData.TaskId}).{detail}",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        var videoUrl = TryGetThalamVideoUrl(result.Root);
        if (string.IsNullOrWhiteSpace(videoUrl))
            return new VideoOperationErrorResult
            {
                Error = $"Thalam video task completed but returned no video_url (task_id={operationData.TaskId}).",
                ProviderMetadata = metadata,
                Response = response
            };

        var downloaded = await DownloadThalamMediaAsync(videoUrl, "video/mp4", cancellationToken);
        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = NormalizeThalamVideoMediaType(downloaded.MediaType, videoUrl),
                Data = Convert.ToBase64String(downloaded.Bytes)
            }],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static List<object> GetThalamVideoWarnings(VideoRequest request)
    {
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
        return warnings;
    }

    private static string EncodeThalamVideoOperation(string taskId, string model)
    {
        var json = JsonSerializer.Serialize(new ThalamVideoOperationData(taskId, model), ThalamVideoOperationJson);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return ThalamVideoOperationTokenPrefix + base64Url;
    }

    private static ThalamVideoOperationData DecodeThalamVideoOperation(string operation)
    {
        if (!operation.StartsWith(ThalamVideoOperationTokenPrefix, StringComparison.Ordinal))
            return new ThalamVideoOperationData(Uri.UnescapeDataString(operation), null);

        var base64Url = operation[ThalamVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<ThalamVideoOperationData>(json, ThalamVideoOperationJson);
            if (data is null || string.IsNullOrWhiteSpace(data.TaskId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The Thalam video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The Thalam video operation token is invalid.", nameof(operation), ex);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The Thalam video operation token is invalid.", nameof(operation), ex);
        }
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
