using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Apertis;

public partial class ApertisProvider
{
    private const string ApertisVideoOperationTokenPrefix = "apv1_";
    private const string ApertisVideoCreateRoute = "create";
    private const string ApertisVideosRoute = "videos";

    private static readonly JsonSerializerOptions ApertisVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (!IsApertisVeoModel(request.Model))
            throw new ArgumentException($"Apertis video operations support only Veo models, not '{request.Model}'.", nameof(request));

        var route = UsesApertisVideosApi(request.Model) ? ApertisVideosRoute : ApertisVideoCreateRoute;
        var warnings = new List<object>();
        using var createRequest = route == ApertisVideosRoute
            ? BuildApertisVideosRequest(request, warnings)
            : BuildApertisVideoCreateRequest(request, warnings);

        using var createResponse = await _client.SendAsync(createRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"Apertis video create failed ({(int)createResponse.StatusCode})."
                : $"Apertis video create failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var root = createDoc.RootElement.Clone();
        var taskId = ApertisTryGetString(root, "id");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Apertis video create response did not contain a task id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeApertisVideoOperation(taskId, request.Model, route),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = ReadApertisUnixTimestamp(root, "status_update_time")
                    ?? ReadApertisUnixTimestamp(root, "created_at")
                    ?? DateTime.UtcNow,
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

        var operationData = DecodeApertisVideoOperation(operation);
        ApplyAuthHeader();

        var statusUri = operationData.Route == ApertisVideosRoute
            ? $"v1/videos/{Uri.EscapeDataString(operationData.TaskId)}"
            : $"v1/video/query?id={Uri.EscapeDataString(operationData.TaskId)}";

        using var pollRequest = new HttpRequestMessage(HttpMethod.Get, statusUri);
        using var pollResponse = await _client.SendAsync(pollRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var pollRaw = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(pollRaw)
                ? $"Apertis video poll failed ({(int)pollResponse.StatusCode})."
                : $"Apertis video poll failed ({(int)pollResponse.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var status = ApertisTryGetString(root, "status")?.Trim().ToLowerInvariant();
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var response = new HeaderResponseData
        {
            Timestamp = ReadApertisUnixTimestamp(root, "status_update_time") ?? DateTime.UtcNow,
            Headers = pollResponse.GetHeaders(),
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (status is "failed" or "cancelled" or "canceled")
        {
            var error = ApertisTryGetString(root, "error", "message")
                ?? $"Apertis video generation failed with status '{status}'.";
            return new VideoOperationErrorResult
            {
                Error = error,
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (status is not "completed" and not "succeeded" and not "success")
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        var videoUrl = ApertisTryGetString(root, "video_url", "url");
        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            return new VideoOperationErrorResult
            {
                Error = $"Apertis video task '{operationData.TaskId}' completed without a video_url.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        using var videoResponse = await _client.GetAsync(videoUrl, cancellationToken);
        var bytes = await videoResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResponse.IsSuccessStatusCode || bytes.Length == 0)
            throw new InvalidOperationException($"Failed to download Apertis video from returned URL ({(int)videoResponse.StatusCode}).");

        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    MediaType = videoResponse.Content.Headers.ContentType?.MediaType
                        ?? GuessApertisVideoMediaType(videoUrl)
                        ?? "video/mp4",
                    Data = Convert.ToBase64String(bytes)
                }
            ],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private HttpRequestMessage BuildApertisVideoCreateRequest(VideoRequest request, List<object> warnings)
    {
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildApertisVideoCreatePayload(request, metadata, warnings);
        return new HttpRequestMessage(HttpMethod.Post, "v1/video/create")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ApertisVideoJsonOptions),
                Encoding.UTF8,
                MediaTypeHeaderValue.Parse(MediaTypeNames.Application.Json))
        };
    }

    private static HttpRequestMessage BuildApertisVideosRequest(VideoRequest request, List<object> warnings)
    {
        var image = ResolveApertisVideoFiles(request).FirstOrDefault()
            ?? throw new ArgumentException($"Apertis model '{request.Model}' requires an input reference image.", nameof(request));
        var form = new MultipartFormDataContent();
        AddApertisVideoFormString(form, "model", request.Model);
        AddApertisVideoFormString(form, "prompt", request.Prompt);
        AddApertisVideoFormString(form, "seconds", (request.Duration ?? 5).ToString(CultureInfo.InvariantCulture));
        AddApertisVideoFormString(form, "size", NormalizeApertisVideosSize(request.Resolution, request.AspectRatio));

        var metadata = request.GetProviderMetadata<JsonElement>("apertis");
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (property.NameEquals("model") || property.NameEquals("prompt")
                    || property.NameEquals("seconds") || property.NameEquals("size")
                    || property.NameEquals("input_reference"))
                    continue;
                AddApertisVideoFormString(form, property.Name, ApertisJsonElementToFormValue(property.Value));
            }
        }

        var bytes = Convert.FromBase64String(image.Data.RemoveDataUrlPrefix());
        var imageContent = new ByteArrayContent(bytes);
        imageContent.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(image.MediaType) ? MediaTypeNames.Image.Png : image.MediaType);
        form.Add(imageContent, "input_reference", $"reference{ApertisImageExtension(image.MediaType)}");

        if (ResolveApertisVideoFiles(request).Skip(1).Any())
            warnings.Add(new { type = "unsupported", feature = "multiple_reference_images", details = "The Apertis /v1/videos Veo API accepts one input_reference." });
        AddApertisVideoUnsupportedWarnings(request, warnings);
        return new HttpRequestMessage(HttpMethod.Post, "v1/videos") { Content = form };
    }

    private static Dictionary<string, object?> BuildApertisVideoCreatePayload(
        VideoRequest request,
        JsonElement metadata,
        List<object> warnings)
    {
        var payload = ApertisJsonObjectToDictionary(metadata);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["size"] = request.Resolution;
        if (request.Duration.HasValue)
            payload["duration"] = request.Duration.Value;

        var images = ResolveApertisVideoFiles(request).Select(NormalizeApertisVideoImage).ToList();
        if (images.Count > 0)
            payload["images"] = images;

        AddApertisVideoUnsupportedWarnings(request, warnings);
        return payload;
    }

    private static void AddApertisVideoUnsupportedWarnings(VideoRequest request, List<object> warnings)
    {
        if (request.Seed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Fps.HasValue)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N.HasValue && request.N.Value > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });
    }

    private static IEnumerable<VideoFile> ResolveApertisVideoFiles(VideoRequest request)
    {
        if (request.Image is not null)
            yield return request.Image;
        if (request.InputReferences is not null)
            foreach (var reference in request.InputReferences)
                yield return reference;
        if (request.FrameImages is not null)
            foreach (var frame in request.FrameImages)
                if (frame?.Image is not null)
                    yield return frame.Image;
    }

    private static string NormalizeApertisVideoImage(VideoFile file)
    {
        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;

        return file.Data.ToDataUrl(string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType);
    }

    private static string NormalizeApertisVideosSize(string? resolution, string? aspectRatio)
    {
        if (!string.IsNullOrWhiteSpace(resolution))
            return resolution.Replace(':', 'x');
        return string.Equals(aspectRatio, "9:16", StringComparison.OrdinalIgnoreCase) ? "720x1280" : "16x9";
    }

    private static void AddApertisVideoFormString(MultipartFormDataContent form, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            form.Add(new StringContent(value, Encoding.UTF8), name);
    }

    private static bool IsApertisVeoModel(string model)
        => model.StartsWith("veo", StringComparison.OrdinalIgnoreCase);

    private static bool UsesApertisVideosApi(string model)
        => model.Equals("veo_3_1", StringComparison.OrdinalIgnoreCase)
            || model.Equals("veo_3_1-fast", StringComparison.OrdinalIgnoreCase);

    private static string EncodeApertisVideoOperation(string taskId, string model, string route)
    {
        var json = JsonSerializer.Serialize(new ApertisVideoOperationData(taskId, model, route), ApertisVideoJsonOptions);
        return ApertisVideoOperationTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static ApertisVideoOperationData DecodeApertisVideoOperation(string operation)
    {
        if (!operation.StartsWith(ApertisVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The Apertis video operation token is invalid.", nameof(operation));

        var base64 = operation[ApertisVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
        if (base64.Length % 4 != 0)
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4), '=');

        try
        {
            var data = JsonSerializer.Deserialize<ApertisVideoOperationData>(
                Encoding.UTF8.GetString(Convert.FromBase64String(base64)), ApertisVideoJsonOptions);
            if (data is null || string.IsNullOrWhiteSpace(data.TaskId) || string.IsNullOrWhiteSpace(data.Model)
                || data.Route is not (ApertisVideoCreateRoute or ApertisVideosRoute))
                throw new ArgumentException("The Apertis video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The Apertis video operation token is invalid.", nameof(operation), ex);
        }
    }

    private sealed record ApertisVideoOperationData(string TaskId, string Model, string Route);

    private static string? GuessApertisVideoMediaType(string? url)
    {
        var normalized = url?.Trim().ToLowerInvariant();
        if (normalized is null) return null;
        if (normalized.Contains(".webm")) return "video/webm";
        if (normalized.Contains(".mov")) return "video/quicktime";
        if (normalized.Contains(".mkv")) return "video/x-matroska";
        if (normalized.Contains(".avi")) return "video/x-msvideo";
        if (normalized.Contains(".mp4")) return "video/mp4";
        return null;
    }
}
