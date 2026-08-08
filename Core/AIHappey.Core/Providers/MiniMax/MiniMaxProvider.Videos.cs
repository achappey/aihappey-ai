using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.MiniMax;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MiniMax;

public partial class MiniMaxProvider
{
    private const string MiniMaxVideoOperationTokenPrefix = "mmv1_";
    private const string MiniMaxVideoV2OperationTokenPrefix = "mmv2_";

    private static readonly JsonSerializerOptions VideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (IsMiniMaxH3Model(request.Model))
            return await StartH3VideoOperation(request, cancellationToken);

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });

        var payload = BuildMiniMaxVideoPayload(request, warnings, GetIdentifier());

        var json = JsonSerializer.Serialize(payload, VideoJson);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v1/video_generation")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"MiniMax video_generation failed ({(int)createResp.StatusCode})"
                : $"MiniMax video_generation failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);

        EnsureBaseResponseOk(createDoc.RootElement, "video_generation");

        var taskId = createDoc.RootElement.TryGetProperty("task_id", out var taskEl)
            ? taskEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("MiniMax video generation returned no task_id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeMiniMaxVideoOperation(taskId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { taskId, status = "Preparing" }),
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

        var operationData = DecodeMiniMaxVideoOperation(operation);
        var taskId = operationData.TaskId;

        if (string.Equals(operationData.Version, "v2", StringComparison.OrdinalIgnoreCase)
            || IsMiniMaxH3Model(operationData.Model))
            return await GetH3VideoOperationStatus(operationData, cancellationToken);

        ApplyAuthHeader();
        using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"v1/query/video_generation?task_id={Uri.EscapeDataString(taskId)}");
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MiniMax video_generation poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        EnsureBaseResponseOk(root, "video_generation_query");
        var status = TryGetStatus(root);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = string.IsNullOrWhiteSpace(operationData.Model)
                ? GetIdentifier()
                : operationData.Model.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { taskId, status });

        if (string.Equals(status, "Fail", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationErrorResult { Error = $"MiniMax video generation failed (task_id={taskId}).", ProviderMetadata = metadata, Response = response };

        if (!string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        var fileId = root.TryGetProperty("file_id", out var fileElement) ? fileElement.ToString() : null;
        if (string.IsNullOrWhiteSpace(fileId))
            return new VideoOperationErrorResult { Error = $"MiniMax task '{taskId}' succeeded but returned no file_id.", ProviderMetadata = metadata, Response = response };

        var downloadUrl = await ResolveDownloadUrlAsync(fileId, cancellationToken);
        using var videoResponse = await _client.GetAsync(downloadUrl, cancellationToken);
        var bytes = await videoResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"MiniMax video download failed ({(int)videoResponse.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = videoResponse.Content.Headers.ContentType?.MediaType ?? GuessVideoMediaType(downloadUrl) ?? "video/mp4",
                Data = Convert.ToBase64String(bytes)
            }],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static string EncodeMiniMaxVideoOperation(string taskId, string model)
    {
        var json = JsonSerializer.Serialize(new MiniMaxVideoOperationData(taskId, model), VideoJson);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return MiniMaxVideoOperationTokenPrefix + base64Url;
    }

    private static string EncodeMiniMaxVideoV2Operation(string taskId, string model)
    {
        var json = JsonSerializer.Serialize(new MiniMaxVideoOperationData(taskId, model, "v2"), VideoJson);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return MiniMaxVideoV2OperationTokenPrefix + base64Url;
    }

    private static MiniMaxVideoOperationData DecodeMiniMaxVideoOperation(string operation)
    {
        var isV1 = operation.StartsWith(MiniMaxVideoOperationTokenPrefix, StringComparison.Ordinal);
        var isV2 = operation.StartsWith(MiniMaxVideoV2OperationTokenPrefix, StringComparison.Ordinal);
        if (!isV1 && !isV2)
            return new MiniMaxVideoOperationData(Uri.UnescapeDataString(operation), null);

        var prefixLength = isV2 ? MiniMaxVideoV2OperationTokenPrefix.Length : MiniMaxVideoOperationTokenPrefix.Length;
        var base64Url = operation[prefixLength..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<MiniMaxVideoOperationData>(json, VideoJson);
            if (data is null || string.IsNullOrWhiteSpace(data.TaskId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The MiniMax video operation token is invalid.", nameof(operation));

            return isV2 ? data with { Version = "v2" } : data;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The MiniMax video operation token is invalid.", nameof(operation), ex);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The MiniMax video operation token is invalid.", nameof(operation), ex);
        }
    }

    private sealed record MiniMaxVideoOperationData(string TaskId, string? Model, string? Version = null);

    private static bool IsMiniMaxH3Model(string? model)
        => !string.IsNullOrWhiteSpace(model)
            && string.Equals(NormalizeModelName(model), "MiniMax-H3", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object?> BuildMiniMaxVideoPayload(
        VideoRequest request,
        List<object> warnings,
        string providerId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = NormalizeModelName(request.Model)
        };

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        AddMiniMaxVideoFrameImages(payload, request);

        if (string.IsNullOrWhiteSpace(request.Prompt)
            && !payload.ContainsKey("first_frame_image")
            && !payload.ContainsKey("last_frame_image"))
        {
            throw new ArgumentException("Prompt or image is required.", nameof(request));
        }

        if (request.InputReferences?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "input_references" });

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        var metadata = GetVideoProviderMetadata<MiniMaxVideoProviderMetadata>(request, providerId);

        if (metadata?.PromptOptimizer is not null)
            payload["prompt_optimizer"] = metadata.PromptOptimizer;

        if (metadata?.FastPretreatment is not null)
            payload["fast_pretreatment"] = metadata.FastPretreatment;

        return payload;
    }

    private static void AddMiniMaxVideoFrameImages(Dictionary<string, object?> payload, VideoRequest request)
    {
        var frameImages = request.FrameImages?.ToList() ?? [];
        VideoFile? firstFrame = null;
        VideoFile? lastFrame = null;

        foreach (var frameImage in frameImages)
        {
            if (frameImage?.Image is null)
                throw new InvalidOperationException("MiniMax video frameImages entries must include an image.");

            if (IsMiniMaxFirstFrame(frameImage.FrameType))
            {
                if (firstFrame is not null)
                    throw new InvalidOperationException("MiniMax video generation supports only one first_frame image.");

                firstFrame = frameImage.Image;
            }
            else if (IsMiniMaxLastFrame(frameImage.FrameType))
            {
                if (lastFrame is not null)
                    throw new InvalidOperationException("MiniMax video generation supports only one last_frame image.");

                lastFrame = frameImage.Image;
            }
            else
            {
                throw new InvalidOperationException($"Unsupported MiniMax video frameType '{frameImage.FrameType}'. Use 'first_frame' or 'last_frame'.");
            }
        }

        firstFrame ??= request.Image;

        if (firstFrame is not null)
            payload["first_frame_image"] = NormalizeMiniMaxVideoImage(firstFrame);

        if (lastFrame is not null)
            payload["last_frame_image"] = NormalizeMiniMaxVideoImage(lastFrame);
    }

    private static string NormalizeMiniMaxVideoImage(VideoFile image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (string.IsNullOrWhiteSpace(image.Data))
            throw new InvalidOperationException("MiniMax video image data is required.");

        var data = image.Data.Trim();
        if (data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return data;
        }

        if (string.IsNullOrWhiteSpace(image.MediaType))
            throw new InvalidOperationException("MiniMax video image mediaType is required for base64 image data.");

        return data.ToDataUrl(image.MediaType);
    }

    private static bool IsMiniMaxFirstFrame(string? frameType)
        => string.Equals(frameType, "first_frame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "firstFrame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "first", StringComparison.OrdinalIgnoreCase);

    private static bool IsMiniMaxLastFrame(string? frameType)
        => string.Equals(frameType, "last_frame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "lastFrame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "last", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetStatus(JsonElement root)
    {
        return root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString()
            : null;
    }

    private async Task<string> ResolveDownloadUrlAsync(string fileId, CancellationToken cancellationToken)
    {
        using var retrieveReq = new HttpRequestMessage(HttpMethod.Get, $"v1/files/retrieve?file_id={fileId}");
        using var retrieveResp = await _client.SendAsync(retrieveReq, cancellationToken);
        var retrieveRaw = await retrieveResp.Content.ReadAsStringAsync(cancellationToken);

        if (!retrieveResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MiniMax file retrieve failed ({(int)retrieveResp.StatusCode}): {retrieveRaw}");

        using var retrieveDoc = JsonDocument.Parse(retrieveRaw);
        EnsureBaseResponseOk(retrieveDoc.RootElement, "file_retrieve");

        var fileObj = retrieveDoc.RootElement.TryGetProperty("file", out var fileEl) ? fileEl : default;
        var downloadUrl = fileObj.ValueKind == JsonValueKind.Object
            && fileObj.TryGetProperty("download_url", out var urlEl)
            && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("MiniMax retrieve file response contained no download_url.");

        return downloadUrl;
    }

    private static void EnsureBaseResponseOk(JsonElement root, string operation)
    {
        if (!root.TryGetProperty("base_resp", out var baseResp) || baseResp.ValueKind != JsonValueKind.Object)
            return;

        if (!baseResp.TryGetProperty("status_code", out var statusCodeEl) || statusCodeEl.ValueKind != JsonValueKind.Number)
            return;

        var statusCode = statusCodeEl.GetInt32();
        if (statusCode == 0)
            return;

        var statusMsg = baseResp.TryGetProperty("status_msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
            ? msgEl.GetString()
            : "MiniMax request failed";

        throw new InvalidOperationException($"MiniMax {operation} failed (status_code={statusCode}, status_msg={statusMsg}).");
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }

    private static T? GetVideoProviderMetadata<T>(VideoRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return default;

        if (!request.ProviderOptions.TryGetValue(providerId, out var element))
            return default;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }
}
