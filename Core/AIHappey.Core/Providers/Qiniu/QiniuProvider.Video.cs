using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Qiniu;

public partial class QiniuProvider
{
    private const string QiniuVideoOperationTokenPrefix = "qnv1_";

    private static readonly JsonSerializerOptions QiniuVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        // Qiniu evolves its model-specific options independently. Preserve its
        // provider object verbatim instead of introducing a fixed metadata DTO.
        var payload = CopyQiniuProviderOptions(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (request.Duration is not null)
            payload["seconds"] = request.Duration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["size"] = request.Resolution;
        if (request.Image is not null)
            payload["input_reference"] = NormalizeQiniuInput(request.Image);

        var imageList = BuildQiniuImageList(request);
        if (imageList.Count > 0)
            payload["image_list"] = imageList;

        var warnings = new List<object>();
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.GenerateAudio is not null) warnings.Add(new { type = "unsupported", feature = "generateAudio" });

        ApplyAuthHeader();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, QiniuVideoJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var raw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Qiniu video creation failed ({(int)createResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var id = ReadQiniuString(root, "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Qiniu video creation returned no id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeQiniuVideoOperation(id, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = ReadQiniuUnixTime(root, "created_at") ?? DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeQiniuVideoOperation(operation);
        ApplyAuthHeader();
        using var statusResponse = await _client.GetAsync($"v1/videos/{Uri.EscapeDataString(operationData.Id)}", cancellationToken);
        var raw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!statusResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Qiniu video status failed ({(int)statusResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = ReadQiniuString(root, "status")?.Trim().ToLowerInvariant();
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var response = new HeaderResponseData
        {
            Timestamp = ReadQiniuUnixTime(root, "completed_at")
                ?? ReadQiniuUnixTime(root, "updated_at")
                ?? ReadQiniuUnixTime(root, "created_at")
                ?? DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            // The create-time model is authoritative and survives status
            // responses that omit or rewrite the model.
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (status is "failed" or "cancelled" or "canceled")
            return new VideoOperationErrorResult
            {
                Error = ReadQiniuNestedString(root, "error", "message") ?? $"Qiniu video generation failed with status '{status}'.",
                ProviderMetadata = metadata,
                Response = response
            };

        if (status is not "completed")
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = response };

        var urls = ReadQiniuVideoUrls(root);
        if (urls.Count == 0)
            return new VideoOperationErrorResult
            {
                Error = $"Qiniu video task '{operationData.Id}' completed but returned no video URL.",
                ProviderMetadata = metadata,
                Response = response
            };

        var videos = new List<VideoOperationVideoData>();
        foreach (var url in urls)
        {
            using var downloadResponse = await _downloadClient.GetAsync(url, cancellationToken);
            var bytes = await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!downloadResponse.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"Qiniu video download failed ({(int)downloadResponse.StatusCode}).");
            videos.Add(new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = downloadResponse.Content.Headers.ContentType?.MediaType ?? GuessQiniuVideoMediaType(url),
                Data = Convert.ToBase64String(bytes)
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

    private static Dictionary<string, object?> CopyQiniuProviderOptions(JsonElement source)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (source.ValueKind != JsonValueKind.Object)
            return payload;
        foreach (var property in source.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
        return payload;
    }

    private static List<Dictionary<string, object?>> BuildQiniuImageList(VideoRequest request)
    {
        var images = new List<Dictionary<string, object?>>();
        foreach (var reference in request.InputReferences ?? [])
            images.Add(new() { ["image"] = NormalizeQiniuInput(reference) });
        foreach (var frame in request.FrameImages ?? [])
            images.Add(new()
            {
                ["image"] = NormalizeQiniuInput(frame.Image),
                ["type"] = string.Equals(frame.FrameType, "last_frame", StringComparison.OrdinalIgnoreCase)
                    ? "end_frame"
                    : "first_frame"
            });
        return images;
    }

    private static string NormalizeQiniuInput(VideoFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Qiniu video input data cannot be empty.", nameof(file));
        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = file.Data.IndexOf(',');
            return comma >= 0 ? file.Data[(comma + 1)..] : file.Data;
        }
        return file.Data;
    }

    private static string EncodeQiniuVideoOperation(string id, string model)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = id, ["model"] = model }, QiniuVideoJson);
        return QiniuVideoOperationTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static (string Id, string Model) DecodeQiniuVideoOperation(string operation)
    {
        if (!operation.StartsWith(QiniuVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The Qiniu video operation token is invalid. Start a new operation to obtain a model-aware token.", nameof(operation));
        try
        {
            var base64 = operation[QiniuVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            if (base64.Length % 4 != 0)
                base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4), '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
            var id = ReadQiniuString(document.RootElement, "id");
            var model = ReadQiniuString(document.RootElement, "model");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("The Qiniu video operation token is invalid.", nameof(operation));
            return (id, model);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The Qiniu video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static List<string> ReadQiniuVideoUrls(JsonElement root)
    {
        var urls = new List<string>();
        if (!root.TryGetProperty("task_result", out var result) || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array)
            return urls;
        foreach (var video in videos.EnumerateArray())
        {
            var url = ReadQiniuString(video, "url");
            if (!string.IsNullOrWhiteSpace(url)) urls.Add(url);
        }
        return urls;
    }

    private static string? ReadQiniuString(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadQiniuNestedString(JsonElement root, string parent, string child)
        => root.TryGetProperty(parent, out var value) && value.ValueKind == JsonValueKind.Object ? ReadQiniuString(value, child) : null;

    private static DateTime? ReadQiniuUnixTime(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;

    private static string GuessQiniuVideoMediaType(string url)
        => Path.GetExtension(Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url).ToLowerInvariant() switch
        {
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            _ => "video/mp4"
        };
}
