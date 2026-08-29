using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AllToken;

public partial class AllTokenProvider
{
    private const string AllTokenVideoOperationPrefix = "atv1_";
    private static readonly JsonSerializerOptions AllTokenVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));
        ApplyAuthHeader();
        var warnings = new List<object>();
        if (request.Fps.HasValue) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        var payload = BuildAllTokenVideoPayload(request, warnings);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos/generations")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AllTokenVideoJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(createRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureAllTokenVideoSuccess(response, raw, "create");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var taskId = ReadAllTokenVideoString(root, "id") ?? throw new InvalidOperationException("AllToken video response missing id.");
        return new VideoOperationStartResult
        {
            Operation = EncodeAllTokenVideoOperation(taskId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = ReadAllTokenVideoTimestamp(root), Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("A video operation is required.", nameof(operation));
        var operationData = DecodeAllTokenVideoOperation(operation);
        ApplyAuthHeader();
        using var response = await _client.GetAsync($"v1/videos/generations/{Uri.EscapeDataString(operationData.TaskId)}", cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureAllTokenVideoSuccess(response, raw, "poll");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = ReadAllTokenVideoString(root, "status") ?? "queued";
        var model = !string.IsNullOrWhiteSpace(operationData.Model) ? operationData.Model : ReadAllTokenVideoString(root, "model");
        var header = new HeaderResponseData
        {
            Timestamp = ReadAllTokenVideoTimestamp(root), Headers = response.GetHeaders(),
            ModelId = string.IsNullOrWhiteSpace(model) ? GetIdentifier() : model.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        if (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("expired", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationErrorResult { Error = ReadAllTokenVideoError(root, status), ProviderMetadata = metadata, Response = header };
        if (!status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = header };
        var url = ReadAllTokenVideoString(root, "video_url");
        if (string.IsNullOrWhiteSpace(url))
            return new VideoOperationErrorResult { Error = $"AllToken video task '{operationData.TaskId}' completed without video_url.", ProviderMetadata = metadata, Response = header };
        using var download = await _uploadClient.GetAsync(url, cancellationToken);
        var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!download.IsSuccessStatusCode) throw new InvalidOperationException($"AllToken video download failed ({(int)download.StatusCode}).");
        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64", Data = Convert.ToBase64String(bytes),
                MediaType = download.Content.Headers.ContentType?.MediaType ?? GuessAllTokenVideoMediaType(url)
            }],
            Warnings = [], ProviderMetadata = metadata, Response = header
        };
    }

    private static Dictionary<string, object?> BuildAllTokenVideoPayload(VideoRequest request, List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model, ["prompt"] = request.Prompt, ["duration"] = request.Duration,
            ["ratio"] = request.AspectRatio, ["resolution"] = request.Resolution,
            ["seed"] = request.Seed, ["generate_audio"] = request.GenerateAudio
        };
        var content = new List<object>();
        if (request.Image is not null) content.Add(ToAllTokenVideoContent(request.Image, "image_url", "first_frame"));
        foreach (var frame in request.FrameImages ?? [])
            content.Add(ToAllTokenVideoContent(frame.Image, "image_url", NormalizeAllTokenVideoRole(frame.FrameType)));
        foreach (var reference in request.InputReferences ?? [])
        {
            var type = reference.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video_url"
                : reference.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "audio_url" : "image_url";
            var role = type switch { "video_url" => "reference_video", "audio_url" => "reference_audio", _ => "reference_image" };
            content.Add(ToAllTokenVideoContent(reference, type, role));
        }
        if (content.Count > 0) payload["content"] = content;
        if (request.ProviderOptions is not null && request.ProviderOptions.TryGetValue("alltoken", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            foreach (var property in metadata.EnumerateObject())
                if (!payload.ContainsKey(property.Name)) payload[property.Name] = property.Value.Clone();
        return payload;
    }

    private static object ToAllTokenVideoContent(VideoFile file, string type, string role)
    {
        var url = file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? file.Data : $"data:{file.MediaType};base64,{file.Data}";
        return type switch
        {
            "video_url" => new { type, video_url = new { url }, role },
            "audio_url" => (object)new { type, audio_url = new { url }, role },
            _ => new { type, image_url = new { url }, role }
        };
    }

    private static string NormalizeAllTokenVideoRole(string? role) => role?.ToLowerInvariant() switch
    {
        "last" or "last_frame" => "last_frame", _ => "first_frame"
    };

    private static string EncodeAllTokenVideoOperation(string taskId, string model)
    {
        var json = JsonSerializer.Serialize(new AllTokenVideoOperation(taskId, model), AllTokenVideoJson);
        return AllTokenVideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static AllTokenVideoOperation DecodeAllTokenVideoOperation(string operation)
    {
        if (!operation.StartsWith(AllTokenVideoOperationPrefix, StringComparison.Ordinal))
            return new AllTokenVideoOperation(Uri.UnescapeDataString(operation), null);
        var value = operation[AllTokenVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
        var padding = value.Length % 4;
        if (padding != 0) value = value.PadRight(value.Length + 4 - padding, '=');
        try
        {
            var data = JsonSerializer.Deserialize<AllTokenVideoOperation>(Encoding.UTF8.GetString(Convert.FromBase64String(value)), AllTokenVideoJson);
            if (data is null || string.IsNullOrWhiteSpace(data.TaskId) || string.IsNullOrWhiteSpace(data.Model)) throw new JsonException();
            return data;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The AllToken video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static void EnsureAllTokenVideoSuccess(HttpResponseMessage response, string raw, string operation)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"AllToken video {operation} failed ({(int)response.StatusCode}): {raw}");
    }

    private static string? ReadAllTokenVideoString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime ReadAllTokenVideoTimestamp(JsonElement root)
    {
        if (DateTime.TryParse(ReadAllTokenVideoString(root, "created_at"), out var created)) return created.ToUniversalTime();
        if (DateTime.TryParse(ReadAllTokenVideoString(root, "completed_at"), out var completed)) return completed.ToUniversalTime();
        return DateTime.UtcNow;
    }

    private static string ReadAllTokenVideoError(JsonElement root, string status)
        => root.TryGetProperty("error", out var error) ? $"AllToken video generation {status}: {error.GetRawText()}" : $"AllToken video generation {status}.";

    private static string GuessAllTokenVideoMediaType(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && Path.GetExtension(uri.AbsolutePath).Equals(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm" : "video/mp4";

    private sealed record AllTokenVideoOperation(string TaskId, string? Model);
}
