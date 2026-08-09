using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.OpenRouter;

public partial class OpenRouterProvider
{
    private const string OpenRouterVideoOperationTokenPrefix = "orv1_";

    private static readonly JsonSerializerOptions OpenRouterVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record OpenRouterVideoOperationData(string JobId, string Model);

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        var payload = BuildOpenRouterVideoPayload(request);
        var json = JsonSerializer.Serialize(payload, OpenRouterVideoJsonOptions);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"OpenRouter video create failed ({(int)createResp.StatusCode})."
                : $"OpenRouter video create failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();

        var jobId = ReadOpenRouterVideoString(createRoot, "id")
            ?? ReadOpenRouterVideoString(createRoot, "generation_id");

        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("OpenRouter video create response contained no id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeOpenRouterVideoOperation(jobId, request.Model),
            Warnings = warnings,
            ProviderMetadata = BuildOpenRouterVideoProviderMetadata(createRoot),
            Response = new()
            {
                Timestamp = now,
                Headers = createResp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var operationData = DecodeOpenRouterVideoOperation(operation);
        var jobId = operationData.JobId;

        using var pollReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/videos/{Uri.EscapeDataString(jobId)}");
        using var pollResp = await _client.SendAsync(pollReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);

        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(pollRaw)
                ? $"OpenRouter video poll failed ({(int)pollResp.StatusCode})."
                : $"OpenRouter video poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var status = ReadOpenRouterVideoString(root, "status") ?? "unknown";
        var metadata = BuildOpenRouterVideoProviderMetadata(root);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = pollResp.GetHeaders(),
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (status.Equals("pending", StringComparison.OrdinalIgnoreCase)
            || status.Equals("in_progress", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationPendingResult
            {
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (!status.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            var error = ReadOpenRouterVideoString(root, "error");
            return new VideoOperationErrorResult
            {
                Error = string.IsNullOrWhiteSpace(error)
                    ? $"OpenRouter video generation ended with status '{status}' (id={jobId})."
                    : error,
                ProviderMetadata = metadata,
                Response = response
            };
        }

        var videos = await DownloadOpenRouterVideosAsync(jobId, root, cancellationToken);
        if (videos.Count == 0)
        {
            return new VideoOperationErrorResult
            {
                Error = $"OpenRouter video task completed but returned no downloadable content (id={jobId}).",
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

    private static Dictionary<string, object?> BuildOpenRouterVideoPayload(VideoRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model
        };

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        if (request.Seed is not null)
            payload["seed"] = request.Seed;

        if (request.GenerateAudio is not null)
            payload["generate_audio"] = request.GenerateAudio;

        AddOpenRouterVideoImageInputs(payload, request);

        MergeOpenRouterVideoProviderOptions(payload, request);

        return payload;
    }

    private static void AddOpenRouterVideoImageInputs(Dictionary<string, object?> payload, VideoRequest request)
    {
        var frameImages = request.FrameImages?.ToList() ?? [];
        if (frameImages.Count > 0)
            payload["frame_images"] = frameImages.Select(ToOpenRouterFrameImage).ToList();

        var inputReferences = request.InputReferences?.ToList() ?? [];
        if (inputReferences.Count == 0 && request.Image is not null)
            inputReferences.Add(request.Image);

        if (inputReferences.Count > 0)
            payload["input_references"] = inputReferences.Select(ToOpenRouterImageReference).ToList();
    }

    private static Dictionary<string, object?> ToOpenRouterFrameImage(VideoFrameImage frameImage)
    {
        if (frameImage?.Image is null)
            throw new InvalidOperationException("OpenRouter video frameImages entries must include an image.");

        return new Dictionary<string, object?>
        {
            ["type"] = "image_url",
            ["image_url"] = new Dictionary<string, object?>
            {
                ["url"] = NormalizeOpenRouterImageUrl(frameImage.Image)
            },
            ["frame_type"] = NormalizeOpenRouterFrameType(frameImage.FrameType)
        };
    }

    private static Dictionary<string, object?> ToOpenRouterImageReference(VideoFile image)
    {
        if (image is null)
            throw new InvalidOperationException("OpenRouter video inputReferences entries must include an image.");

        return new Dictionary<string, object?>
        {
            ["type"] = "image_url",
            ["image_url"] = new Dictionary<string, object?>
            {
                ["url"] = NormalizeOpenRouterImageUrl(image)
            }
        };
    }

    private static string NormalizeOpenRouterFrameType(string? frameType)
    {
        if (string.Equals(frameType, "first_frame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "firstFrame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "first", StringComparison.OrdinalIgnoreCase))
        {
            return "first_frame";
        }

        if (string.Equals(frameType, "last_frame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "lastFrame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "last", StringComparison.OrdinalIgnoreCase))
        {
            return "last_frame";
        }

        throw new InvalidOperationException($"Unsupported OpenRouter video frameType '{frameType}'. Use 'first_frame' or 'last_frame'.");
    }

    private static void MergeOpenRouterVideoProviderOptions(Dictionary<string, object?> payload, VideoRequest request)
    {
        if (request.ProviderOptions is null)
            return;

        if (!request.ProviderOptions.TryGetValue("openrouter", out var providerOptions))
            return;

        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private async Task<List<VideoOperationVideoData>> DownloadOpenRouterVideosAsync(
        string jobId,
        JsonElement completedRoot,
        CancellationToken cancellationToken)
    {
        var outputCount = GetOpenRouterUnsignedUrlCount(completedRoot);
        if (outputCount <= 0)
            outputCount = 1;

        List<VideoOperationVideoData> videos = [];
        for (var index = 0; index < outputCount; index++)
        {
            using var contentReq = new HttpRequestMessage(
                HttpMethod.Get,
                $"v1/videos/{Uri.EscapeDataString(jobId)}/content?index={index}");

            using var contentResp = await _client.SendAsync(contentReq, cancellationToken);
            var bytes = await contentResp.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!contentResp.IsSuccessStatusCode)
            {
                var errorRaw = Encoding.UTF8.GetString(bytes);
                throw new InvalidOperationException($"OpenRouter video content download failed ({(int)contentResp.StatusCode}, index={index}): {errorRaw}");
            }

            var mediaType = contentResp.Content.Headers.ContentType?.MediaType
                ?? GuessOpenRouterVideoMediaType(GetOpenRouterUnsignedUrl(completedRoot, index))
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

    private Dictionary<string, JsonElement> BuildOpenRouterVideoProviderMetadata(JsonElement root)
    {
        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                id = ReadOpenRouterVideoString(root, "id"),
                generationId = ReadOpenRouterVideoString(root, "generation_id"),
                status = ReadOpenRouterVideoString(root, "status"),
                pollingUrl = ReadOpenRouterVideoString(root, "polling_url"),
                usage = root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                    ? usage.Clone()
                    : (JsonElement?)null
            }, JsonSerializerOptions.Web)
        };

        if (root.TryGetProperty("usage", out var gatewayUsage)
            && gatewayUsage.ValueKind == JsonValueKind.Object
            && gatewayUsage.TryGetProperty("cost", out var cost)
            && cost.ValueKind == JsonValueKind.Number
            && cost.TryGetDecimal(out var parsedCost))
        {
            providerMetadata["gateway"] = JsonSerializer.SerializeToElement(new { cost = parsedCost }, JsonSerializerOptions.Web);
        }

        return providerMetadata;
    }

    private static string EncodeOpenRouterVideoOperation(string jobId, string model)
    {
        var json = JsonSerializer.Serialize(new OpenRouterVideoOperationData(jobId, model), OpenRouterVideoJsonOptions);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return OpenRouterVideoOperationTokenPrefix + base64Url;
    }

    private static OpenRouterVideoOperationData DecodeOpenRouterVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        if (!operation.StartsWith(OpenRouterVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The OpenRouter video operation token is invalid.", nameof(operation));

        var base64Url = operation[OpenRouterVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<OpenRouterVideoOperationData>(json, OpenRouterVideoJsonOptions);
            if (data is null || string.IsNullOrWhiteSpace(data.JobId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The OpenRouter video operation token is invalid.", nameof(operation));

            return data;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The OpenRouter video operation token is invalid.", nameof(operation), ex);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The OpenRouter video operation token is invalid.", nameof(operation), ex);
        }
    }

    private static string NormalizeOpenRouterImageUrl(VideoFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (string.IsNullOrWhiteSpace(file.Data))
            throw new InvalidOperationException("OpenRouter video image data is required.");

        var data = file.Data.Trim();
        if (data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return data;
        }

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Image.Png
            : file.MediaType;

        return $"data:{mediaType};base64,{data}";
    }

    private static int GetOpenRouterUnsignedUrlCount(JsonElement root)
    {
        if (!root.TryGetProperty("unsigned_urls", out var unsignedUrls)
            || unsignedUrls.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return unsignedUrls.GetArrayLength();
    }

    private static string? GetOpenRouterUnsignedUrl(JsonElement root, int index)
    {
        if (!root.TryGetProperty("unsigned_urls", out var unsignedUrls)
            || unsignedUrls.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var i = 0;
        foreach (var item in unsignedUrls.EnumerateArray())
        {
            if (i == index && item.ValueKind == JsonValueKind.String)
                return item.GetString();

            i++;
        }

        return null;
    }

    private static string? ReadOpenRouterVideoString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? GuessOpenRouterVideoMediaType(string? url)
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
