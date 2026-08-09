using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LLMGateway;

public partial class LLMGatewayProvider
{
    private const string LLMGatewayVideoOperationTokenPrefix = "llmgwv1_";

    private static readonly JsonSerializerOptions LLMGatewayVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
        var payload = BuildLLMGatewayVideoPayload(request);
        var createJson = JsonSerializer.Serialize(payload, LLMGatewayVideoJsonOptions);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = new StringContent(createJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"LLM Gateway video create failed ({(int)createResp.StatusCode})."
                : $"LLM Gateway video create failed ({(int)createResp.StatusCode}): {createRaw}");
        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();
        var videoId = ReadLLMGatewayVideoString(createRoot, "id")
            ?? ReadLLMGatewayVideoString(createRoot, "video_id")
            ?? ReadLLMGatewayVideoString(createRoot, "videoId")
            ?? ReadLLMGatewayVideoString(createRoot, "generation_id")
            ?? ReadLLMGatewayVideoString(createRoot, "job_id");

        if (string.IsNullOrWhiteSpace(videoId))
            throw new InvalidOperationException("LLM Gateway video create response contained no video id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeLLMGatewayVideoOperation(videoId, request.Model),
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { videoId, create = createRoot }),
            Response = new()
            {
                Timestamp = ResolveLLMGatewayVideoTimestamp(createRoot, DateTime.UtcNow),
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

        var operationData = DecodeLLMGatewayVideoOperation(operation);
        ApplyAuthHeader();

        using var pollReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/videos/{Uri.EscapeDataString(operationData.VideoId)}");
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLM Gateway video poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var status = ReadLLMGatewayVideoString(root, "status") ?? "unknown";
        var polledModel = ReadLLMGatewayVideoString(root, "model");
        var effectiveModel = string.IsNullOrWhiteSpace(polledModel) ? operationData.Model : polledModel;
        var response = new HeaderResponseData
        {
            Timestamp = ResolveLLMGatewayVideoTimestamp(root, DateTime.UtcNow),
            Headers = pollResp.GetHeaders(),
            ModelId = string.IsNullOrWhiteSpace(effectiveModel)
                ? GetIdentifier()
                : effectiveModel.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            videoId = operationData.VideoId,
            status,
            poll = root
        });

        if (!IsLLMGatewayVideoTerminalStatus(status))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        if (!IsLLMGatewayVideoSuccessStatus(status))
        {
            var error = ReadLLMGatewayVideoError(root);
            return new VideoOperationErrorResult
            {
                Error = string.IsNullOrWhiteSpace(error)
                    ? $"LLM Gateway video generation failed with status '{status}' (id={operationData.VideoId})."
                    : error,
                ProviderMetadata = metadata,
                Response = response
            };
        }

        var videos = await DownloadLLMGatewayOperationVideosAsync(operationData.VideoId, root, cancellationToken);
        if (videos.Count == 0)
            return new VideoOperationErrorResult
            {
                Error = $"LLM Gateway video task completed but returned no downloadable content (id={operationData.VideoId}).",
                ProviderMetadata = metadata,
                Response = response
            };

        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "LLM Gateway video generation is asynchronous. Use StartVideoOperation and GetVideoOperationStatus.");

    private static Dictionary<string, object?> BuildLLMGatewayVideoPayload(VideoRequest request)
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

        if (request.Seed is not null)
            payload["seed"] = request.Seed;

        if (request.Fps is not null)
            payload["fps"] = request.Fps;

        if (request.N is not null)
            payload["n"] = request.N;

        if (request.Image is not null)
            payload["image"] = NormalizeLLMGatewayVideoImage(request.Image);

        MergeLLMGatewayVideoProviderOptions(payload, request);

        return payload;
    }

    private static void MergeLLMGatewayVideoProviderOptions(Dictionary<string, object?> payload, VideoRequest request)
    {
        if (request.ProviderOptions is null)
            return;

        if (!request.ProviderOptions.TryGetValue("llmgateway", out var providerOptions))
            return;

        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
        {
            if (string.Equals(property.Name, "async", StringComparison.OrdinalIgnoreCase))
                continue;

            payload[property.Name] = property.Value.Clone();
        }
    }

    private static object NormalizeLLMGatewayVideoImage(VideoFile file)
    {
        var url = file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? file.Data
            : $"data:{(string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType)};base64,{file.Data}";

        return new
        {
            type = "image_url",
            image_url = new
            {
                url
            }
        };
    }

    private async Task<List<VideoOperationVideoData>> DownloadLLMGatewayOperationVideosAsync(
        string videoId,
        JsonElement completedRoot,
        CancellationToken cancellationToken)
    {
        var outputCount = GetLLMGatewayVideoOutputCount(completedRoot);
        if (outputCount <= 0)
            outputCount = 1;

        List<VideoOperationVideoData> videos = [];
        for (var index = 0; index < outputCount; index++)
        {
            var path = outputCount > 1
                ? $"v1/videos/{Uri.EscapeDataString(videoId)}/content?index={index}"
                : $"v1/videos/{Uri.EscapeDataString(videoId)}/content";

            using var contentReq = new HttpRequestMessage(HttpMethod.Get, path);
            using var contentResp = await _client.SendAsync(contentReq, cancellationToken);
            var bytes = await contentResp.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!contentResp.IsSuccessStatusCode)
            {
                var errorRaw = Encoding.UTF8.GetString(bytes);
                throw new InvalidOperationException($"LLM Gateway video content download failed ({(int)contentResp.StatusCode}, index={index}): {errorRaw}");
            }

            var mediaType = contentResp.Content.Headers.ContentType?.MediaType
                ?? GuessLLMGatewayVideoMediaType(GetLLMGatewayVideoOutputUrl(completedRoot, index))
                ?? "video/mp4";

            videos.Add(new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = mediaType,
                Data = Convert.ToBase64String(bytes)
            });
        }

        return videos;
    }

    private static string EncodeLLMGatewayVideoOperation(string videoId, string model)
    {
        var json = JsonSerializer.Serialize(
            new LLMGatewayVideoOperationData(videoId, model),
            LLMGatewayVideoJsonOptions);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return LLMGatewayVideoOperationTokenPrefix + base64Url;
    }

    private static LLMGatewayVideoOperationData DecodeLLMGatewayVideoOperation(string operation)
    {
        if (!operation.StartsWith(LLMGatewayVideoOperationTokenPrefix, StringComparison.Ordinal))
            return new LLMGatewayVideoOperationData(Uri.UnescapeDataString(operation), null);

        var base64Url = operation[LLMGatewayVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<LLMGatewayVideoOperationData>(json, LLMGatewayVideoJsonOptions);
            if (data is null || string.IsNullOrWhiteSpace(data.VideoId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The LLM Gateway video operation token is invalid.", nameof(operation));

            return data;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The LLM Gateway video operation token is invalid.", nameof(operation), exception);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The LLM Gateway video operation token is invalid.", nameof(operation), exception);
        }
    }

    private sealed record LLMGatewayVideoOperationData(string VideoId, string? Model);

    private static int GetLLMGatewayVideoOutputCount(JsonElement root)
    {
        if (root.TryGetProperty("unsigned_urls", out var unsignedUrls) && unsignedUrls.ValueKind == JsonValueKind.Array)
            return unsignedUrls.GetArrayLength();

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            return output.GetArrayLength();

        if (root.TryGetProperty("videos", out var videos) && videos.ValueKind == JsonValueKind.Array)
            return videos.GetArrayLength();

        return 0;
    }

    private static string? GetLLMGatewayVideoOutputUrl(JsonElement root, int index)
    {
        var unsignedUrl = GetLLMGatewayIndexedString(root, "unsigned_urls", index);
        if (!string.IsNullOrWhiteSpace(unsignedUrl))
            return unsignedUrl;

        return GetLLMGatewayIndexedVideoUrl(root, "output", index)
            ?? GetLLMGatewayIndexedVideoUrl(root, "videos", index)
            ?? ReadLLMGatewayVideoString(root, "url")
            ?? ReadLLMGatewayVideoString(root, "video_url")
            ?? ReadLLMGatewayVideoString(root, "videoUrl");
    }

    private static string? GetLLMGatewayIndexedString(JsonElement root, string propertyName, int index)
    {
        if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        var i = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (i == index && item.ValueKind == JsonValueKind.String)
                return item.GetString();

            i++;
        }

        return null;
    }

    private static string? GetLLMGatewayIndexedVideoUrl(JsonElement root, string propertyName, int index)
    {
        if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        var i = 0;
        foreach (var item in arr.EnumerateArray())
        {
            if (i == index)
            {
                if (item.ValueKind == JsonValueKind.String)
                    return item.GetString();

                if (item.ValueKind == JsonValueKind.Object)
                    return ReadLLMGatewayVideoString(item, "url")
                        ?? ReadLLMGatewayVideoString(item, "video_url")
                        ?? ReadLLMGatewayVideoString(item, "videoUrl");
            }

            i++;
        }

        return null;
    }

    private static string? ReadLLMGatewayVideoString(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = root.TryGetString(propertyName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? ReadLLMGatewayVideoError(JsonElement root)
    {
        var direct = ReadLLMGatewayVideoString(root, "error", "message", "detail");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            return ReadLLMGatewayVideoString(error, "message", "detail", "code");

        return null;
    }

    private static bool IsLLMGatewayVideoTerminalStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.Equals("completed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("complete", StringComparison.OrdinalIgnoreCase)
               || status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
               || status.Equals("success", StringComparison.OrdinalIgnoreCase)
               || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("error", StringComparison.OrdinalIgnoreCase)
               || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
               || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
               || status.Equals("expired", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLLMGatewayVideoSuccessStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.Equals("completed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("complete", StringComparison.OrdinalIgnoreCase)
               || status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
               || status.Equals("success", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime ResolveLLMGatewayVideoTimestamp(JsonElement root, DateTime fallback)
    {
        var value = ReadLLMGatewayVideoString(root, "created_at", "createdAt", "updated_at", "updatedAt");

        return DateTime.TryParse(value, out var parsed)
            ? parsed.ToUniversalTime()
            : fallback;
    }

    private static string? GuessLLMGatewayVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";
        if (url.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase))
            return "video/x-m4v";
        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }
}
