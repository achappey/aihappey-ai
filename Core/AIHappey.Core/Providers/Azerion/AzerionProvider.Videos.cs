using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Azerion;

public partial class AzerionProvider
{
    private const string VideoTaskEndpoint = "v1/contents/generations/tasks";
    private const string VideoOperationTokenPrefix = "azv1_";

    private static readonly JsonSerializerOptions AzerionVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record AzerionVideoOperationData(string TaskId, string? Model);

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)
            && request.Image is null
            && request.FrameImages?.Any() != true
            && request.InputReferences?.Any() != true)
            throw new ArgumentException("Prompt or image is required.", nameof(request));

        List<object> warnings = [];
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var payload = BuildVideoTaskPayload(request);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, VideoTaskEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, AzerionVideoJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var raw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Azerion video task creation failed ({(int)createResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var taskId = ReadString(root, "id") ?? ReadString(root, "task_id")
            ?? throw new InvalidOperationException("Azerion video task creation returned no task id.");
        var status = ReadString(root, "status") ?? "queued";

        return new VideoOperationStartResult
        {
            Operation = EncodeVideoOperation(taskId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { taskId, status, task = root }),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeVideoOperation(operation);
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{VideoTaskEndpoint}/{Uri.EscapeDataString(operationData.TaskId)}");
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Azerion video task poll failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = ReadString(root, "status") ?? "unknown";
        var providerModel = ReadString(root, "model");
        var model = string.IsNullOrWhiteSpace(providerModel) ? operationData.Model : providerModel;
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            taskId = operationData.TaskId,
            status,
            task = root
        });
        var responseData = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = response.GetHeaders(),
            ModelId = string.IsNullOrWhiteSpace(model)
                ? GetIdentifier()
                : model.ToModelId(GetIdentifier())
        };

        if (!IsTerminalStatus(status))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = responseData };

        if (!IsSuccessStatus(status))
        {
            return new VideoOperationErrorResult
            {
                Error = $"Azerion video task '{operationData.TaskId}' failed with status '{status}': {ReadTaskError(root)}",
                ProviderMetadata = metadata,
                Response = responseData
            };
        }

        var videos = await ExtractOperationVideosAsync(root, cancellationToken);
        if (videos.Count == 0)
        {
            return new VideoOperationErrorResult
            {
                Error = $"Azerion video task '{operationData.TaskId}' completed but returned no video content.",
                ProviderMetadata = metadata,
                Response = responseData
            };
        }

        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = responseData
        };
    }

    private static Dictionary<string, object?> BuildVideoTaskPayload(VideoRequest request)
    {
        var payload = new Dictionary<string, object?>();
        if (request.ProviderOptions?.TryGetValue("azerion", out var metadata) == true
            && metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        var content = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrWhiteSpace(request.Prompt))
            content.Add(new() { ["type"] = "text", ["text"] = request.Prompt });

        if (request.Image is not null)
            content.Add(BuildImageContent(request.Image, null));

        foreach (var frame in request.FrameImages ?? [])
            content.Add(BuildImageContent(frame.Image, frame.FrameType));

        foreach (var reference in request.InputReferences ?? [])
            content.Add(BuildImageContent(reference, "reference_image"));

        payload["model"] = request.Model.Trim();
        payload["content"] = content;

        if (request.Duration is not null)
            payload["duration"] = request.Duration;
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["ratio"] = request.AspectRatio;
        if (request.GenerateAudio is not null)
            payload["generate_audio"] = request.GenerateAudio;

        return payload;
    }

    private static Dictionary<string, object?> BuildImageContent(VideoFile file, string? role)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Video image data is required.", nameof(file));

        var url = file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? file.Data
                : file.Data.ToDataUrl(file.MediaType);

        return new Dictionary<string, object?>
        {
            ["type"] = "image_url",
            ["image_url"] = new Dictionary<string, object?> { ["url"] = url },
            ["role"] = string.IsNullOrWhiteSpace(role) ? null : role
        };
    }

    private async Task<List<VideoOperationVideoData>> ExtractOperationVideosAsync(
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var candidates = FindVideoCandidates(root);
        List<VideoOperationVideoData> videos = [];

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Base64))
            {
                videos.Add(new()
                {
                    Type = "base64",
                    MediaType = candidate.MediaType ?? "video/mp4",
                    Data = candidate.Base64
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(candidate.Url))
                continue;

            using var download = await _client.GetAsync(candidate.Url, cancellationToken);
            var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!download.IsSuccessStatusCode)
                throw new InvalidOperationException($"Azerion video download failed ({(int)download.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

            videos.Add(new()
            {
                Type = "base64",
                MediaType = download.Content.Headers.ContentType?.MediaType ?? candidate.MediaType ?? GuessVideoMediaType(candidate.Url) ?? "video/mp4",
                Data = Convert.ToBase64String(bytes)
            });
        }

        return videos;
    }

    private static List<(string? Base64, string? Url, string? MediaType)> FindVideoCandidates(JsonElement root)
    {
        List<(string?, string?, string?)> results = [];
        Visit(root);
        return results;

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    Visit(item);
                return;
            }

            if (element.ValueKind != JsonValueKind.Object)
                return;

            var base64 = ReadString(element, "base64_encoded") ?? ReadString(element, "b64_json");
            var url = ReadString(element, "url");
            var mediaType = ReadString(element, "mime_type") ?? ReadString(element, "media_type");
            if (!string.IsNullOrWhiteSpace(base64)
                || (!string.IsNullOrWhiteSpace(url) && LooksLikeVideo(url, mediaType)))
                results.Add((base64, url, mediaType));

            foreach (var property in element.EnumerateObject())
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    Visit(property.Value);
        }
    }

    private static bool LooksLikeVideo(string url, string? mediaType)
        => mediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true
            || url.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".webm", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".mov", StringComparison.OrdinalIgnoreCase);

    private static string? GuessVideoMediaType(string? url)
        => url?.Contains(".webm", StringComparison.OrdinalIgnoreCase) == true ? "video/webm"
            : url?.Contains(".mov", StringComparison.OrdinalIgnoreCase) == true ? "video/quicktime"
            : url?.Contains(".mp4", StringComparison.OrdinalIgnoreCase) == true ? "video/mp4"
            : null;

    private static string EncodeVideoOperation(string taskId, string model)
    {
        var json = JsonSerializer.Serialize(new AzerionVideoOperationData(taskId, model), AzerionVideoJson);
        return VideoOperationTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static AzerionVideoOperationData DecodeVideoOperation(string operation)
    {
        if (!operation.StartsWith(VideoOperationTokenPrefix, StringComparison.Ordinal))
            return new(Uri.UnescapeDataString(operation), null);

        var base64 = operation[VideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
        if (base64.Length % 4 != 0)
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4), '=');

        try
        {
            var data = JsonSerializer.Deserialize<AzerionVideoOperationData>(
                Encoding.UTF8.GetString(Convert.FromBase64String(base64)),
                AzerionVideoJson);
            if (data is null || string.IsNullOrWhiteSpace(data.TaskId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The Azerion video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The Azerion video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static bool IsSuccessStatus(string status)
        => status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
            || status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("success", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalStatus(string status)
        => IsSuccessStatus(status)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("expired", StringComparison.OrdinalIgnoreCase);

    private static string ReadTaskError(JsonElement root)
        => root.TryGetProperty("error", out var error) ? error.ToString()
            : ReadString(root, "message") ?? "No error details were returned.";
}
