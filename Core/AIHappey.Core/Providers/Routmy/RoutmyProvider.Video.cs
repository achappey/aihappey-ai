using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Routmy;

public partial class RoutmyProvider
{
    private const string RoutmyVideoGenerationsEndpoint = "v1/video/generations";
    private const string RoutmyVideoOperationTokenPrefix = "rmv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();
        var submittedAt = DateTime.UtcNow;
        var payload = BuildRoutmyVideoPayload(request);
        var json = JsonSerializer.Serialize(payload, RoutmyMediaJsonOptions);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, RoutmyVideoGenerationsEndpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(
            createRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw CreateRoutmyVideoException("submission", createResponse, createRaw);

        using var createDocument = JsonDocument.Parse(createRaw);
        var createRoot = createDocument.RootElement.Clone();
        var taskId = FindRoutmyVideoString(createRoot, "task_id", "taskId", "id", "request_id", "requestId");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Routmy video submission response did not contain a task id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeRoutmyVideoOperation(taskId, request.Model),
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(createRoot),
            Response = new()
            {
                Timestamp = ResolveRoutmyCreatedTimestamp(createRoot) ?? submittedAt,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var (taskId, model) = DecodeRoutmyVideoOperation(operation);
        ApplyAuthHeader();

        using var statusRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"{RoutmyVideoGenerationsEndpoint}/{Uri.EscapeDataString(taskId)}");
        using var statusResponse = await _client.SendAsync(
            statusRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var statusRaw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!statusResponse.IsSuccessStatusCode)
            throw CreateRoutmyVideoException("status", statusResponse, statusRaw);

        using var statusDocument = JsonDocument.Parse(statusRaw);
        var root = statusDocument.RootElement.Clone();
        var state = FindRoutmyVideoString(root, "status", "state") ?? "unknown";
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var response = new HeaderResponseData
        {
            Timestamp = ResolveRoutmyCreatedTimestamp(root) ?? DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            ModelId = model.ToModelId(GetIdentifier())
        };

        if (IsFailedRoutmyVideoState(state))
        {
            return new VideoOperationErrorResult
            {
                Error = FindRoutmyVideoString(root, "error", "message", "detail", "fail_reason", "failure_reason")
                    ?? $"Routmy video generation failed with status '{state}' (task_id={taskId}).",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (!IsSuccessfulRoutmyVideoState(state))
        {
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = metadata,
                Response = response
            };
        }

        var videos = await ExtractRoutmyVideosAsync(root, cancellationToken);
        if (videos.Count == 0)
        {
            return new VideoOperationErrorResult
            {
                Error = $"Routmy video task completed without video output (task_id={taskId}).",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        return new VideoOperationCompletedResult
        {
            Videos = videos.Select(video => new VideoOperationVideoData
            {
                Type = video.Type,
                Data = video.Data,
                MediaType = video.MediaType
            }),
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static Dictionary<string, object?> BuildRoutmyVideoPayload(VideoRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (request.N is not null)
            payload["n"] = request.N;

        if (request.Seed is not null)
            payload["seed"] = request.Seed;

        if (request.Fps is not null)
            payload["fps"] = request.Fps;

        if (request.GenerateAudio is not null)
            payload["audio"] = request.GenerateAudio;

        if (request.Image is not null)
            payload["input_image"] = ToRoutmyMediaValue(request.Image, MediaTypeNames.Image.Png);

        AddRoutmyFrameImages(payload, request.FrameImages);
        AddRoutmyInputReferences(payload, request.InputReferences);
        MergeRoutmyProviderOptions(payload, request.ProviderOptions, RoutmyVideoProtectedKeys);

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        return payload;
    }

    private async Task<List<VideoResponseFile>> ExtractRoutmyVideosAsync(JsonElement root, CancellationToken cancellationToken)
    {
        List<VideoResponseFile> videos = [];

        await ExtractRoutmyVideoItemsAsync(root, "videos", videos, cancellationToken);
        if (videos.Count == 0)
            await ExtractRoutmyVideoItemsAsync(root, "data", videos, cancellationToken);

        if (videos.Count == 0
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object)
            videos.AddRange(await ExtractRoutmyVideosAsync(data, cancellationToken));

        if (videos.Count == 0
            && root.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Object)
            videos.AddRange(await ExtractRoutmyVideosAsync(output, cancellationToken));

        if (videos.Count == 0)
        {
            var item = await ExtractRoutmyVideoItemAsync(root, cancellationToken);
            if (item is not null)
                videos.Add(item);
        }

        return videos;
    }

    private async Task ExtractRoutmyVideoItemsAsync(
        JsonElement root,
        string propertyName,
        List<VideoResponseFile> videos,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty(propertyName, out var items) || items.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in items.EnumerateArray())
        {
            var video = await ExtractRoutmyVideoItemAsync(item, cancellationToken);
            if (video is not null)
                videos.Add(video);
        }
    }

    private async Task<VideoResponseFile?> ExtractRoutmyVideoItemAsync(JsonElement item, CancellationToken cancellationToken)
    {
        var b64 = TryGetRoutmyString(item, "b64_json")
            ?? TryGetRoutmyString(item, "base64")
            ?? TryGetRoutmyString(item, "data");

        if (!string.IsNullOrWhiteSpace(b64))
        {
            var mediaType = TryGetRoutmyString(item, "mime_type")
                ?? TryGetRoutmyString(item, "mimeType")
                ?? "video/mp4";

            return new VideoResponseFile
            {
                Type = "base64",
                Data = StripRoutmyDataUrlPrefix(b64),
                MediaType = mediaType
            };
        }

        var url = TryGetRoutmyString(item, "url")
            ?? TryGetRoutmyNestedString(item, "video_url", "url")
            ?? TryGetRoutmyString(item, "result_url");

        if (string.IsNullOrWhiteSpace(url))
            return null;

        var downloaded = await TryFetchRoutmyAsBase64Async(url, cancellationToken);
        if (downloaded is not null)
        {
            return new VideoResponseFile
            {
                Type = "base64",
                Data = downloaded.Value.Base64,
                MediaType = downloaded.Value.MediaType
            };
        }

        return new VideoResponseFile
        {
            Type = "base64",
            Data = url,
            MediaType = TryGetRoutmyString(item, "mime_type")
                ?? TryGetRoutmyString(item, "mimeType")
                ?? GuessRoutmyMediaTypeFromUrl(url, "video/mp4")
        };
    }

    private static void AddRoutmyFrameImages(Dictionary<string, object?> payload, IEnumerable<VideoFrameImage>? frameImages)
    {
        if (frameImages is null)
            return;

        foreach (var frame in frameImages)
        {
            if (frame?.Image is null || string.IsNullOrWhiteSpace(frame.FrameType))
                continue;

            var key = frame.FrameType switch
            {
                "last_frame" => "last_frame_image",
                "lastFrame" => "last_frame_image",
                "last_frame_image" => "last_frame_image",
                _ => "input_image"
            };

            payload[key] = ToRoutmyMediaValue(frame.Image, MediaTypeNames.Image.Png);
        }
    }

    private static void AddRoutmyInputReferences(Dictionary<string, object?> payload, IEnumerable<VideoFile>? inputReferences)
    {
        var references = inputReferences?.Where(reference => reference is not null).ToArray();
        if (references is null || references.Length == 0)
            return;

        var imageReferences = new List<string>();
        var videoReferences = new List<string>();
        var audioReferences = new List<string>();

        foreach (var reference in references)
        {
            var mediaType = reference.MediaType ?? string.Empty;
            var value = ToRoutmyMediaValue(reference, MediaTypeNames.Application.Octet);

            if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                imageReferences.Add(value);
            else if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                audioReferences.Add(value);
            else
                videoReferences.Add(value);
        }

        if (imageReferences.Count == 1 && !payload.ContainsKey("input_image"))
            payload["input_image"] = imageReferences[0];
        else if (imageReferences.Count > 0)
            payload["reference_images"] = imageReferences;

        if (videoReferences.Count == 1)
            payload["input_video"] = videoReferences[0];
        else if (videoReferences.Count > 0)
            payload["reference_videos"] = videoReferences;

        if (audioReferences.Count > 0)
            payload["input_audio"] = audioReferences[0];
    }

    private static string EncodeRoutmyVideoOperation(string taskId, string model)
    {
        var envelope = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["taskId"] = taskId,
            ["model"] = model
        }, RoutmyMediaJsonOptions);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope.GetRawText()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return RoutmyVideoOperationTokenPrefix + encoded;
    }

    private static (string TaskId, string Model) DecodeRoutmyVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));
        if (!operation.StartsWith(RoutmyVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The Routmy video operation token is invalid. Start a new operation to obtain a model-aware token.", nameof(operation));

        try
        {
            var encoded = operation[RoutmyVideoOperationTokenPrefix.Length..]
                .Replace('-', '+')
                .Replace('_', '/');
            var remainder = encoded.Length % 4;
            if (remainder != 0)
                encoded = encoded.PadRight(encoded.Length + 4 - remainder, '=');

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var taskId = TryGetRoutmyString(document.RootElement, "taskId");
            var model = TryGetRoutmyString(document.RootElement, "model");
            if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("The Routmy video operation token is invalid.", nameof(operation));

            return (taskId, model);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The Routmy video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string? FindRoutmyVideoString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            var value = TryGetRoutmyString(root, name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            return FindRoutmyVideoString(data, names);

        if (root.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.Object)
            return FindRoutmyVideoString(task, names);

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            return FindRoutmyVideoString(error, names);

        return null;
    }

    private static bool IsSuccessfulRoutmyVideoState(string state)
        => state.Trim().ToUpperInvariant() is "SUCCESS" or "SUCCEEDED" or "COMPLETED" or "COMPLETE" or "DONE";

    private static bool IsFailedRoutmyVideoState(string state)
        => state.Trim().ToUpperInvariant() is "FAILURE" or "FAILED" or "ERROR" or "CANCELED" or "CANCELLED";

    private static InvalidOperationException CreateRoutmyVideoException(
        string operation,
        HttpResponseMessage response,
        string content)
        => new(string.IsNullOrWhiteSpace(content)
            ? $"Routmy video {operation} request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
            : $"Routmy video {operation} request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {content}");

    private static string ToRoutmyMediaValue(VideoFile file, string fallbackMediaType)
    {
        var value = file.Data;
        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return value;

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? fallbackMediaType : file.MediaType;
        return $"data:{mediaType};base64,{value}";
    }

    private static string StripRoutmyDataUrlPrefix(string value)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return value;

        var comma = value.IndexOf(',');
        return comma >= 0 ? value[(comma + 1)..] : value;
    }

    private static readonly HashSet<string> RoutmyVideoProtectedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "model",
        "prompt"
    };
}
