using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PiAPI;

public partial class PiAPIProvider
{
    private const string PiApiVideoOperationTokenPrefix = "pav1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = CreatePiApiVideoWarnings(request);
        var task = await CreateMediaTaskAsync(
            request.Model,
            "txt2video",
            CreatePiApiVideoInput(request),
            request.ProviderOptions,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(task.TaskId))
            throw new InvalidOperationException("PiAPI video task creation response did not contain data.task_id.");

        return new VideoOperationStartResult
        {
            Operation = EncodePiApiVideoOperation(task.TaskId, request.Model),
            Warnings = warnings,
            ProviderMetadata = CreatePiApiVideoMetadata(task),
            Response = CreatePiApiVideoResponse(request.Model)
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var operationData = DecodePiApiVideoOperation(operation);

        ApplyAuthHeader();
        var task = await GetMediaTaskAsync(operationData.TaskId, cancellationToken);
        var metadata = CreatePiApiVideoMetadata(task);
        var response = CreatePiApiVideoResponse(operationData.Model);

        if (IsPiApiFailedVideoTask(task.Status))
        {
            return new VideoOperationErrorResult
            {
                Error = GetPiApiVideoTaskError(task, operationData.TaskId),
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (!IsCompletedTask(task.Status))
        {
            return new VideoOperationPendingResult
            {
                ProviderMetadata = metadata,
                Response = response
            };
        }

        List<VideoOperationVideoData> videos = [];
        foreach (var output in GetOutputValues(task.Root, "video", "video_url", "video_urls", "videos").Distinct())
        {
            var video = await DownloadMediaAsync(output, "video/mp4", cancellationToken);
            videos.Add(new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = video.MimeType,
                Data = video.Base64
            });
        }

        if (videos.Count == 0)
        {
            return new VideoOperationErrorResult
            {
                Error = $"PiAPI video task '{operationData.TaskId}' completed without generated video.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static List<object> CreatePiApiVideoWarnings(VideoRequest request)
    {
        List<object> warnings = [];
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "PiAPI task outputs are provider-defined." });

        return warnings;
    }

    private static Dictionary<string, object?> CreatePiApiVideoInput(VideoRequest request)
    {
        var input = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["duration"] = request.Duration,
            ["resolution"] = request.Resolution,
            ["aspect_ratio"] = request.AspectRatio,
            ["seed"] = request.Seed
        };

        var imageUrls = new List<string>();
        var videoUrls = new List<string>();
        var audioUrls = new List<string>();
        AddVideoFile(request.Image, imageUrls, videoUrls, audioUrls);
        foreach (var reference in request.InputReferences ?? [])
            AddVideoFile(reference, imageUrls, videoUrls, audioUrls);
        foreach (var frame in request.FrameImages ?? [])
            AddVideoFile(frame.Image, imageUrls, videoUrls, audioUrls);

        if (imageUrls.Count > 0)
            input["image_urls"] = imageUrls;
        if (videoUrls.Count > 0)
            input["video_urls"] = videoUrls;
        if (audioUrls.Count > 0)
            input["audio_urls"] = audioUrls;

        if (request.FrameImages?.Any() == true && imageUrls.Count is > 0 and <= 2)
            input["mode"] = "first_last_frames";
        else if (imageUrls.Count > 0 || videoUrls.Count > 0 || audioUrls.Count > 0)
            input["mode"] = "omni_reference";
        else
            input["mode"] = "text_to_video";

        return input;
    }

    private static string EncodePiApiVideoOperation(string taskId, string model)
    {
        var json = JsonSerializer.Serialize(
            new PiApiVideoOperationData(taskId, model),
            PiApiMediaJsonOptions);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return PiApiVideoOperationTokenPrefix + base64Url;
    }

    private static PiApiVideoOperationData DecodePiApiVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));
        if (!operation.StartsWith(PiApiVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The PiAPI video operation token is invalid.", nameof(operation));

        var base64Url = operation[PiApiVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<PiApiVideoOperationData>(json, PiApiMediaJsonOptions);
            if (data is null || string.IsNullOrWhiteSpace(data.TaskId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The PiAPI video operation token is invalid.", nameof(operation));

            return data;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The PiAPI video operation token is invalid.", nameof(operation), ex);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The PiAPI video operation token is invalid.", nameof(operation), ex);
        }
    }

    private HeaderResponseData CreatePiApiVideoResponse(string model)
        => new()
        {
            Timestamp = DateTime.UtcNow,
            ModelId = ToPiApiModelId(model).ToModelId(GetIdentifier())
        };

    private Dictionary<string, JsonElement> CreatePiApiVideoMetadata(PiApiTaskResult task)
        => GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            taskId = task.TaskId,
            status = task.Status,
            task = task.Root
        });

    private static bool IsPiApiFailedVideoTask(string? status)
        => status is not null && (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("expired", StringComparison.OrdinalIgnoreCase));

    private static string GetPiApiVideoTaskError(PiApiTaskResult task, string taskId)
    {
        var data = GetData(task.Root);
        var error = data.TryGetProperty("error", out var errorElement)
            && errorElement.ValueKind != JsonValueKind.Null
                ? errorElement.GetRawText()
                : task.Root.GetRawText();

        return $"PiAPI video task '{taskId}' failed with status '{task.Status}': {error}";
    }

    private static void AddVideoFile(VideoFile? file, List<string> imageUrls, List<string> videoUrls, List<string> audioUrls)
    {
        if (file is null || string.IsNullOrWhiteSpace(file.Data))
            return;

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType;
        var value = file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? file.Data
                : ToDataUrl(file.Data, mediaType);

        if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            videoUrls.Add(value);
        else if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            audioUrls.Add(value);
        else
            imageUrls.Add(value);
    }

    private sealed record PiApiVideoOperationData(string TaskId, string Model);
}
