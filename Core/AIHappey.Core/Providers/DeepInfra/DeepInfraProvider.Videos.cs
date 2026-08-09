using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    private const string DeepInfraVideoOperationTokenPrefix = "div1_";

    private static readonly JsonSerializerOptions VideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record DeepInfraVideoStatus(string Status, JsonElement Root);
    private sealed record DeepInfraVideoOperationData(string VideoId, string? Model);

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildDeepInfraVideoPayload(request, metadata, warnings);

        var json = JsonSerializer.Serialize(payload, VideoJson);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepInfra video create failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();
        var videoId = createRoot.TryGetString("id")
            ?? throw new InvalidOperationException("DeepInfra video create response missing 'id'.");

        var status = createRoot.TryGetString("status") ?? "queued";
        return new VideoOperationStartResult
        {
            Operation = EncodeDeepInfraVideoOperation(videoId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { videoId, status, job = createRoot }),
            Response = new()
            {
                Timestamp = now,
                Headers = createResp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeDeepInfraVideoOperation(operation);
        ApplyAuthHeader();
        var job = await PollDeepInfraVideoAsync(operationData.VideoId, cancellationToken);
        var providerModel = job.Root.TryGetString("model");
        var model = !string.IsNullOrWhiteSpace(providerModel) ? providerModel : operationData.Model;
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            videoId = operationData.VideoId,
            status = job.Status,
            job = job.Root
        });
        var response = new HeaderResponseData
        {
            Timestamp = ResolveDeepInfraVideoTimestamp(job.Root),
            ModelId = string.IsNullOrWhiteSpace(model)
                ? GetIdentifier()
                : model.ToModelId(GetIdentifier())
        };

        if (!IsDeepInfraVideoTerminalStatus(job.Status))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        if (!IsDeepInfraVideoSuccessStatus(job.Status))
        {
            return new VideoOperationErrorResult
            {
                Error = $"DeepInfra video generation failed with status '{job.Status}': {GetDeepInfraVideoError(job.Root)}",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        var videos = await ExtractDeepInfraOperationVideosAsync(operationData.VideoId, job.Root, cancellationToken);
        if (videos.Count == 0)
        {
            return new VideoOperationErrorResult
            {
                Error = $"DeepInfra video '{operationData.VideoId}' completed but returned no video content.",
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

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        var started = await StartVideoOperation(request, cancellationToken);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var interval = TimeSpan.FromSeconds(Math.Max(1, TryGetDeepInfraVideoInt(metadata, "poll_interval_seconds", "pollIntervalSeconds") ?? 5));
        var timeout = TimeSpan.FromMinutes(Math.Max(1, TryGetDeepInfraVideoInt(metadata, "poll_timeout_minutes", "pollTimeoutMinutes") ?? 10));
        var maxAttempts = TryGetDeepInfraVideoInt(metadata, "poll_max_attempts", "pollMaxAttempts");
        var terminal = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => GetVideoOperationStatus(started.Operation, ct),
            isTerminal: status => status is VideoOperationCompletedResult or VideoOperationErrorResult,
            interval,
            timeout,
            maxAttempts,
            cancellationToken);

        if (terminal is VideoOperationErrorResult error)
            throw new InvalidOperationException(error.Error);
        if (terminal is not VideoOperationCompletedResult completed)
            throw new InvalidOperationException("DeepInfra video operation did not complete.");

        return new VideoResponse
        {
            Videos = completed.Videos.Select(video => new VideoResponseFile
            {
                Type = video.Type,
                MediaType = video.MediaType,
                Data = video.Data as string ?? Convert.ToBase64String((byte[])video.Data!)
            }).ToList(),
            Warnings = started.Warnings.Concat(completed.Warnings),
            ProviderMetadata = completed.ProviderMetadata,
            Response = completed.Response
        };
    }

    private static string EncodeDeepInfraVideoOperation(string videoId, string model)
    {
        var json = JsonSerializer.Serialize(new DeepInfraVideoOperationData(videoId, model), VideoJson);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return DeepInfraVideoOperationTokenPrefix + base64Url;
    }

    private static DeepInfraVideoOperationData DecodeDeepInfraVideoOperation(string operation)
    {
        if (!operation.StartsWith(DeepInfraVideoOperationTokenPrefix, StringComparison.Ordinal))
            return new DeepInfraVideoOperationData(Uri.UnescapeDataString(operation), null);

        var base64Url = operation[DeepInfraVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<DeepInfraVideoOperationData>(json, VideoJson);
            if (data is null || string.IsNullOrWhiteSpace(data.VideoId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The DeepInfra video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The DeepInfra video operation token is invalid.", nameof(operation), ex);
        }
    }

    private Dictionary<string, object?> BuildDeepInfraVideoPayload(VideoRequest request, JsonElement metadata, List<object> warnings)
    {
        var payload = CreateDeepInfraVideoPassthrough(metadata);

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        var negativePrompt = TryGetDeepInfraVideoString(metadata, "negative_prompt", "negativePrompt");
        if (!string.IsNullOrWhiteSpace(negativePrompt))
            payload["negative_prompt"] = negativePrompt;

        var aspectRatio = request.AspectRatio ?? TryGetDeepInfraVideoString(metadata, "aspect_ratio", "aspectRatio");
        if (!string.IsNullOrWhiteSpace(aspectRatio))
            payload["aspect_ratio"] = aspectRatio;

        var size = request.Resolution ?? TryGetDeepInfraVideoString(metadata, "size", "resolution");
        if (!string.IsNullOrWhiteSpace(size))
            payload["size"] = size;

        var seconds = request.Duration ?? TryGetDeepInfraVideoInt(metadata, "seconds", "duration");
        if (seconds is not null)
            payload["seconds"] = seconds.Value;

        var seed = request.Seed ?? TryGetDeepInfraVideoInt(metadata, "seed");
        if (seed is not null)
            payload["seed"] = seed.Value;

        var style = TryGetDeepInfraVideoString(metadata, "style");
        if (!string.IsNullOrWhiteSpace(style))
            payload["style"] = style;

        var imageUrl = ResolveDeepInfraVideoImageUrl(request, metadata, warnings);
        if (!string.IsNullOrWhiteSpace(imageUrl))
            payload["image_url"] = imageUrl;

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps", details = "DeepInfra /v1/videos does not define an fps parameter." });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "DeepInfra /v1/videos does not define a generic output count parameter." });

        if (request.InputReferences?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "inputReferences", details = "DeepInfra /v1/videos only documents a single first-frame image_url." });

        if (request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "frameImages", details = "DeepInfra /v1/videos only documents a single first-frame image_url." });

        return payload;
    }

    private static Dictionary<string, object?> CreateDeepInfraVideoPassthrough(JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>();

        if (metadata.ValueKind != JsonValueKind.Object)
            return payload;

        if (!metadata.TryGetProperty("extra_body", out var extraBody) && !metadata.TryGetProperty("extraBody", out extraBody))
            return payload;

        if (extraBody.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in extraBody.EnumerateObject())
        {
            payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static string? ResolveDeepInfraVideoImageUrl(VideoRequest request, JsonElement metadata, List<object> warnings)
    {
        var imageUrl = TryGetDeepInfraVideoString(metadata, "image_url", "imageUrl");
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            if (request.Image is not null)
                warnings.Add(new { type = "ignored", feature = "image", details = "providerOptions.deepinfra.image_url overrides the local image input." });

            return imageUrl;
        }

        if (request.Image is null)
            return null;

        if (string.IsNullOrWhiteSpace(request.Image.Data))
            throw new ArgumentException("Image data is required.", nameof(request));

        var mediaType = string.IsNullOrWhiteSpace(request.Image.MediaType)
            ? MediaTypeNames.Application.Octet
            : request.Image.MediaType;

        return request.Image.Data.ToDataUrl(mediaType);
    }

    private async Task<DeepInfraVideoStatus> PollDeepInfraVideoAsync(string videoId, CancellationToken cancellationToken)
    {
        using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"v1/videos/{Uri.EscapeDataString(videoId)}");
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);

        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepInfra video poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var status = root.TryGetString("status") ?? "queued";

        return new DeepInfraVideoStatus(status, root);
    }

    private async Task<List<VideoOperationVideoData>> ExtractDeepInfraOperationVideosAsync(
        string videoId,
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var responseFiles = await ExtractDeepInfraVideosAsync(root, cancellationToken);
        if (responseFiles.Count == 0)
        {
            using var contentResponse = await _client.GetAsync(
                $"v1/videos/{Uri.EscapeDataString(videoId)}/content",
                cancellationToken);
            var content = await contentResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!contentResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"DeepInfra video content failed ({(int)contentResponse.StatusCode}): {Encoding.UTF8.GetString(content)}");

            responseFiles.Add(new VideoResponseFile
            {
                MediaType = contentResponse.Content.Headers.ContentType?.MediaType ?? "video/mp4",
                Data = Convert.ToBase64String(content)
            });
        }

        return responseFiles.Select(video => new VideoOperationVideoData
        {
            Type = "base64",
            MediaType = video.MediaType,
            Data = video.Data
        }).ToList();
    }

    private static DateTime ResolveDeepInfraVideoTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("created_at", out var createdAt)
            && createdAt.ValueKind == JsonValueKind.Number
            && createdAt.TryGetInt64(out var unixSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return DateTime.UtcNow;
    }

    private async Task<List<VideoResponseFile>> ExtractDeepInfraVideosAsync(JsonElement root, CancellationToken cancellationToken)
    {
        List<VideoResponseFile> videos = [];

        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var video = await NormalizeDeepInfraVideoOutputAsync(item, cancellationToken);
                    if (video is not null)
                        videos.Add(video);
                }
            }
            else
            {
                var video = await NormalizeDeepInfraVideoOutputAsync(data, cancellationToken);
                if (video is not null)
                    videos.Add(video);
            }
        }

        if (videos.Count == 0)
        {
            var video = await NormalizeDeepInfraVideoOutputAsync(root, cancellationToken);
            if (video is not null)
                videos.Add(video);
        }

        return videos;
    }

    private async Task<VideoResponseFile?> NormalizeDeepInfraVideoOutputAsync(JsonElement item, CancellationToken cancellationToken)
    {
        if (item.ValueKind == JsonValueKind.String)
            return await NormalizeDeepInfraVideoValueAsync(item.GetString(), "video/mp4", cancellationToken);

        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var mediaType = GuessDeepInfraVideoMediaType(item) ?? "video/mp4";
        var base64 = item.TryGetString("b64_json", "base64", "video_base64", "videoBase64", "data");
        if (!string.IsNullOrWhiteSpace(base64))
            return await NormalizeDeepInfraVideoValueAsync(base64, mediaType, cancellationToken);

        var url = item.TryGetString("url", "video_url", "videoUrl", "output_url", "outputUrl");
        if (!string.IsNullOrWhiteSpace(url))
            return await NormalizeDeepInfraVideoValueAsync(url, mediaType, cancellationToken);

        return null;
    }

    private async Task<VideoResponseFile?> NormalizeDeepInfraVideoValueAsync(string? value, string fallbackMediaType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var mediaType = ExtractDeepInfraDataUrlMediaType(value) ?? fallbackMediaType;
            return new VideoResponseFile
            {
                MediaType = mediaType,
                Data = value.RemoveDataUrlPrefix()
            };
        }

        if (LooksLikeDeepInfraHttpUrl(value))
            return await DownloadDeepInfraVideoAsync(value, fallbackMediaType, cancellationToken);

        return new VideoResponseFile
        {
            MediaType = fallbackMediaType,
            Data = value
        };
    }

    private async Task<VideoResponseFile> DownloadDeepInfraVideoAsync(string url, string fallbackMediaType, CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync(url, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var text = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"DeepInfra video download failed ({(int)resp.StatusCode}): {text}");
        }

        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
            mediaType = GuessDeepInfraVideoMediaType(url) ?? fallbackMediaType;

        return new VideoResponseFile
        {
            MediaType = mediaType,
            Data = Convert.ToBase64String(bytes)
        };
    }

    private static bool IsDeepInfraVideoTerminalStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return IsDeepInfraVideoSuccessStatus(status)
            || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeepInfraVideoSuccessStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "done", StringComparison.OrdinalIgnoreCase);

    private static string GetDeepInfraVideoError(JsonElement root)
    {
        var error = root.TryGetString("error", "message", "detail");
        if (!string.IsNullOrWhiteSpace(error))
            return error;

        if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null)
            return errorElement.GetRawText();

        return "Unknown error";
    }

    private static int? TryGetDeepInfraVideoInt(JsonElement metadata, params string[] names)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!metadata.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
                return intValue;

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out intValue))
            {
                return intValue;
            }
        }

        return null;
    }

    private static string? TryGetDeepInfraVideoString(JsonElement metadata, params string[] names)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        return metadata.TryGetString(names);
    }

    private static string? GuessDeepInfraVideoMediaType(JsonElement item)
    {
        var value = item.TryGetString("mime_type", "mimeType", "media_type", "mediaType", "content_type", "contentType", "format");
        return NormalizeDeepInfraVideoMediaType(value);
    }

    private static string? GuessDeepInfraVideoMediaType(string value)
    {
        var lower = value.Split('?', '#')[0].ToLowerInvariant();

        if (lower.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";

        if (lower.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";

        if (lower.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            return "video/x-matroska";

        if (lower.EndsWith(".avi", StringComparison.OrdinalIgnoreCase))
            return "video/x-msvideo";

        if (lower.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || lower.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }

    private static string? NormalizeDeepInfraVideoMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains('/'))
            return normalized;

        return normalized switch
        {
            "mp4" or "mpeg4" or "m4v" => "video/mp4",
            "webm" => "video/webm",
            "mov" or "quicktime" => "video/quicktime",
            "mkv" => "video/x-matroska",
            "avi" => "video/x-msvideo",
            _ => null
        };
    }

    private static string? ExtractDeepInfraDataUrlMediaType(string dataUrl)
    {
        var semicolonIndex = dataUrl.IndexOf(';');
        if (!dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || semicolonIndex <= 5)
            return null;

        return dataUrl[5..semicolonIndex];
    }

    private static bool LooksLikeDeepInfraHttpUrl(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
