using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIsa;

public partial class AIsaProvider
{
    private const string AIsaVideoEndpoint = "v1/video/generations";
    private const string AIsaVideoOperationPrefix = "aisav1_";
    private static readonly JsonSerializerOptions AIsaVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var payload = AIsaJsonObjectToDictionary(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        SetAIsaVideoValue(payload, "duration", request.Duration);
        SetAIsaVideoValue(payload, "resolution", request.Resolution);
        SetAIsaVideoValue(payload, "aspect_ratio", request.AspectRatio);
        SetAIsaVideoValue(payload, "seed", request.Seed);
        SetAIsaVideoValue(payload, "fps", request.Fps);
        SetAIsaVideoValue(payload, "n", request.N);
        SetAIsaVideoValue(payload, "generate_audio", request.GenerateAudio);
        if (request.Image is not null)
            payload["image"] = ToAIsaVideoInput(request.Image);
        if (request.InputReferences?.Any() == true)
            payload["input_references"] = request.InputReferences.Select(ToAIsaVideoInput).ToArray();
        if (request.FrameImages?.Any() == true)
            payload["frame_images"] = request.FrameImages.Select(frame => new Dictionary<string, object?>
            {
                ["frame_type"] = frame.FrameType,
                ["image"] = ToAIsaVideoInput(frame.Image)
            }).ToArray();

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, AIsaVideoEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AIsaVideoJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var root = await ReadAIsaVideoJsonAsync(response, "submission", cancellationToken);
        var taskId = GetAIsaVideoString(root, "task_id") ?? GetAIsaVideoString(root, "id")
            ?? GetAIsaVideoString(GetAIsaVideoData(root), "task_id") ?? GetAIsaVideoString(GetAIsaVideoData(root), "id");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("AIsa video submission did not return a task_id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeAIsaVideoOperation(taskId, request.Model),
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var operationData = DecodeAIsaVideoOperation(operation);
        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{AIsaVideoEndpoint}/{Uri.EscapeDataString(operationData.TaskId)}");
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var root = await ReadAIsaVideoJsonAsync(response, "status", cancellationToken);
        var data = GetAIsaVideoData(root);
        var status = GetAIsaVideoString(data, "status") ?? GetAIsaVideoString(root, "status") ?? "unknown";
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var responseData = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = response.GetHeaders(),
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (IsAIsaFailedVideoStatus(status))
            return new VideoOperationErrorResult
            {
                Error = GetAIsaVideoString(data, "fail_reason") ?? GetAIsaVideoString(data, "error")
                    ?? GetAIsaVideoString(root, "message") ?? $"AIsa video task '{operationData.TaskId}' failed with status '{status}'.",
                ProviderMetadata = metadata,
                Response = responseData
            };

        if (!IsAIsaCompletedVideoStatus(status))
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = responseData };

        var urls = GetAIsaVideoUrls(root).Distinct(StringComparer.Ordinal).ToList();
        if (urls.Count == 0)
            return new VideoOperationErrorResult
            {
                Error = $"AIsa video task '{operationData.TaskId}' completed without a video URL.",
                ProviderMetadata = metadata,
                Response = responseData
            };

        List<VideoOperationVideoData> videos = [];
        foreach (var url in urls)
        {
            using var download = await _client.GetAsync(url, cancellationToken);
            var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!download.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"AIsa video download failed ({(int)download.StatusCode}).");
            videos.Add(new VideoOperationVideoData
            {
                Type = "base64",
                Data = Convert.ToBase64String(bytes),
                MediaType = download.Content.Headers.ContentType?.MediaType ?? AIsaVideoMediaType(url)
            });
        }

        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = responseData
        };
    }

    private static void SetAIsaVideoValue(Dictionary<string, object?> payload, string name, object? value)
    {
        if (value is not null) payload[name] = value;
    }

    private static string ToAIsaVideoInput(VideoFile file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file.Data);
        if (Uri.TryCreate(file.Data, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return file.Data;
        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return file.Data;
        return $"data:{(string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType)};base64,{file.Data}";
    }

    private static JsonElement GetAIsaVideoData(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;

    private static string? GetAIsaVideoString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String) return message.GetString();
        return value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? value.ToString() : null;
    }

    private static IEnumerable<string> GetAIsaVideoUrls(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                foreach (var url in GetAIsaVideoUrls(child)) yield return url;
            yield break;
        }
        if (element.ValueKind != JsonValueKind.Object) yield break;

        foreach (var property in element.EnumerateObject())
        {
            var isVideoUrl = property.Name.Equals("url", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("video_url", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("result_url", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("file_url", StringComparison.OrdinalIgnoreCase);
            if (isVideoUrl && property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") yield return value!;
            }
            else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                foreach (var url in GetAIsaVideoUrls(property.Value)) yield return url;
        }
    }

    private static bool IsAIsaFailedVideoStatus(string status)
        => status.Trim().ToUpperInvariant() is "FAILURE" or "FAILED" or "ERROR" or "CANCELED" or "CANCELLED";

    private static bool IsAIsaCompletedVideoStatus(string status)
        => status.Trim().ToUpperInvariant() is "SUCCESS" or "SUCCEEDED" or "COMPLETED" or "DONE";

    private static string AIsaVideoMediaType(string url)
        => url.Contains(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm" : "video/mp4";

    private static async Task<JsonElement> ReadAIsaVideoJsonAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AIsa video {operation} failed ({(int)response.StatusCode}): {raw}");
        try { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
        catch (JsonException exception) { throw new InvalidOperationException($"AIsa video {operation} returned invalid JSON.", exception); }
    }

    private static string EncodeAIsaVideoOperation(string taskId, string model)
    {
        var json = JsonSerializer.Serialize(new AIsaVideoOperationData(taskId, model), AIsaVideoJsonOptions);
        return AIsaVideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static AIsaVideoOperationData DecodeAIsaVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(AIsaVideoOperationPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A model-aware AIsa video operation token is required.", nameof(operation));
        try
        {
            var value = operation[AIsaVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            var result = JsonSerializer.Deserialize<AIsaVideoOperationData>(Encoding.UTF8.GetString(Convert.FromBase64String(value)), AIsaVideoJsonOptions);
            if (result is null || string.IsNullOrWhiteSpace(result.TaskId) || string.IsNullOrWhiteSpace(result.Model))
                throw new ArgumentException("The AIsa video operation token is invalid.", nameof(operation));
            return result;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The AIsa video operation token is invalid.", nameof(operation), exception);
        }
    }

    private sealed record AIsaVideoOperationData(string TaskId, string Model);
}
