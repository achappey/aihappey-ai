using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vidu;

public partial class ViduProvider
{
    private static readonly JsonSerializerOptions ViduVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record ViduVideoCreationResult(string State, JsonElement RawRoot);

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var hasPrompt = !string.IsNullOrWhiteSpace(request.Prompt);
        var hasImage = request.Image is not null;

        if (!hasPrompt && !hasImage)
            throw new ArgumentException("Prompt or image is required.", nameof(request));

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        var videoMetadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var (endpoint, payload) = BuildViduVideoPayload(request, videoMetadata, warnings);

        var json = JsonSerializer.Serialize(payload, ViduVideoJsonOptions);
        using var startReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var startResp = await _client.SendAsync(startReq, cancellationToken);
        var startRaw = await startResp.Content.ReadAsStringAsync(cancellationToken);
        if (!startResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vidu video request failed ({(int)startResp.StatusCode}): {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var taskId = startDoc.RootElement.TryGetProperty("task_id", out var taskEl)
            ? taskEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Vidu response missing task_id.");

        return new VideoOperationStartResult
        {
            Operation = taskId,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { taskId, state = "pending" }),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        ApplyAuthHeader();
        var result = await PollCreationsAsync(operation, cancellationToken);
        var model = result.RawRoot.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
            ? modelElement.GetString()
            : null;
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = string.IsNullOrWhiteSpace(model) ? GetIdentifier() : model.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.RawRoot.Clone());

        if (string.Equals(result.State, "failed", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationErrorResult { Error = $"Vidu video task failed (task_id={operation}).", ProviderMetadata = metadata, Response = response };

        if (!string.Equals(result.State, "success", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        var creationUrls = GetCreationUrls(result.RawRoot);
        if (creationUrls.Count == 0)
            return new VideoOperationErrorResult { Error = $"Vidu video task completed but returned no creation url (task_id={operation}).", ProviderMetadata = metadata, Response = response };

        var videos = new List<VideoOperationVideoData>();
        foreach (var creationUrl in creationUrls)
        {
            using var fileResp = await _client.GetAsync(creationUrl, cancellationToken);
            var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!fileResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Vidu video download failed ({(int)fileResp.StatusCode}): {Encoding.UTF8.GetString(fileBytes)}");
            videos.Add(new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = fileResp.Content.Headers.ContentType?.MediaType ?? GuessVideoMediaType(creationUrl) ?? "video/mp4",
                Data = Convert.ToBase64String(fileBytes)
            });
        }

        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static (string Endpoint, Dictionary<string, object?> Payload) BuildViduVideoPayload(
        VideoRequest request,
        JsonElement metadata,
        List<object> warnings)
    {
        var frames = request.FrameImages?.ToList() ?? [];
        var references = request.InputReferences?.ToList() ?? [];
        var firstFrames = frames.Where(x => string.Equals(x.FrameType, "first_frame", StringComparison.OrdinalIgnoreCase)).ToList();
        var lastFrames = frames.Where(x => string.Equals(x.FrameType, "last_frame", StringComparison.OrdinalIgnoreCase)).ToList();
        if (firstFrames.Count > 1 || lastFrames.Count > 1)
            throw new ArgumentException("Vidu accepts at most one first_frame and one last_frame.", nameof(request));
        if (frames.Any(x => !string.Equals(x.FrameType, "first_frame", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(x.FrameType, "last_frame", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Vidu frameType must be first_frame or last_frame.", nameof(request));

        string endpoint;
        if (frames.Count > 0)
        {
            if (firstFrames.Count != 1 || lastFrames.Count != 1)
                throw new ArgumentException("Vidu start/end video requires both first_frame and last_frame.", nameof(request));
            endpoint = "start-end2video";
        }
        else if (references.Count > 0)
            endpoint = "reference2video";
        else if (request.Image is not null)
            endpoint = "img2video";
        else
            endpoint = "text2video";

        var payload = CopyViduVideoOptions(metadata);
        payload["model"] = request.Model;

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        if (request.Seed is not null)
            payload["seed"] = request.Seed;

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        if (endpoint == "start-end2video")
        {
            payload["images"] = new[] { ToViduData(frames.First(x => x.FrameType.Equals("first_frame", StringComparison.OrdinalIgnoreCase)).Image), ToViduData(frames.First(x => x.FrameType.Equals("last_frame", StringComparison.OrdinalIgnoreCase)).Image) };
            if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });
        }
        else if (endpoint == "reference2video")
        {
            var imageRefs = references.Where(x => x.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)).Select(ToViduData).ToArray();
            var videoRefs = references.Where(x => x.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)).Select(ToViduData).ToArray();
            if (imageRefs.Length + videoRefs.Length != references.Count)
                throw new ArgumentException("Vidu inputReferences must have an image/* or video/* media type.", nameof(request));
            if (imageRefs.Length > 0) payload["images"] = imageRefs;
            if (videoRefs.Length > 0) payload["videos"] = videoRefs;
        }
        else if (endpoint == "img2video")
        {
            payload["images"] = new[] { ToViduData(request.Image!) };
            if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });
        }

        if (endpoint is "text2video" or "reference2video")
            if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;

        if (request.GenerateAudio is not null)
            payload["audio"] = request.GenerateAudio;

        return (endpoint, payload);
    }

    private static string ToViduData(VideoFile file)
        => file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || Uri.TryCreate(file.Data, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? file.Data
                : file.Data.ToDataUrl(file.MediaType);

    private static Dictionary<string, object?> CopyViduVideoOptions(JsonElement options)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (options.ValueKind != JsonValueKind.Object) return result;
        foreach (var property in options.EnumerateObject())
            result[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText(), ViduVideoJsonOptions);
        return result;
    }

    private async Task<ViduVideoCreationResult> PollCreationsAsync(string taskId, CancellationToken cancellationToken)
    {
        using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"tasks/{taskId}/creations");
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vidu task poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var state = root.TryGetProperty("state", out var stateEl)
            ? stateEl.GetString() ?? "unknown"
            : "unknown";

        return new ViduVideoCreationResult(state, root);
    }

    private static string? TryGetFirstCreationUrl(JsonElement root)
    {
        if (root.TryGetProperty("creations", out var creations)
            && creations.ValueKind == JsonValueKind.Array)
        {
            var first = creations.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object
                && first.TryGetProperty("url", out var urlEl)
                && urlEl.ValueKind == JsonValueKind.String)
            {
                return urlEl.GetString();
            }
        }

        return null;
    }

    private static List<string> GetCreationUrls(JsonElement root)
        => root.TryGetProperty("creations", out var creations) && creations.ValueKind == JsonValueKind.Array
            ? creations.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                .Select(x => x.GetProperty("url").GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList()
            : [];

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

