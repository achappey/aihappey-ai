using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.OpenRouter;

public partial class OpenRouterProvider
{
    private static readonly JsonSerializerOptions OpenRouterVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record OpenRouterVideoPollResult(string Status, string Raw, JsonElement Root);

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

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

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
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

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollOpenRouterVideoAsync(jobId, createRoot, ct),
            isTerminal: r => IsOpenRouterVideoTerminalStatus(r.Status),
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (!IsOpenRouterVideoSuccessStatus(completed.Status))
        {
            var error = ReadOpenRouterVideoString(completed.Root, "error");
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"OpenRouter video generation failed with status '{completed.Status}' (id={jobId}). Response: {completed.Raw}"
                : $"OpenRouter video generation failed with status '{completed.Status}' (id={jobId}): {error}");
        }

        var videos = await DownloadOpenRouterVideosAsync(jobId, completed.Root, cancellationToken);

        if (videos.Count == 0)
            throw new InvalidOperationException($"OpenRouter video task completed but returned no downloadable content (id={jobId}).");


        var providerKey = GetIdentifier();
        var providerMetadata = new Dictionary<string, JsonElement>();

        if (completed.Root.TryGetProperty("usage", out var usageEl)
            && usageEl.ValueKind == JsonValueKind.Object)
        {
            providerMetadata[providerKey] = JsonSerializer.SerializeToElement(new
            {
                usage = usageEl.Clone()
            }, JsonSerializerOptions.Web);
        }

        decimal? cost = null;

        if (completed.Root.TryGetProperty("usage", out var gatewayUsageEl)
            && gatewayUsageEl.ValueKind == JsonValueKind.Object
            && gatewayUsageEl.TryGetProperty("cost", out var costEl)
            && costEl.ValueKind == JsonValueKind.Number
            && costEl.TryGetDecimal(out var parsedCost))
        {
            cost = parsedCost;
        }

        if (cost is not null)
        {
            providerMetadata["gateway"] = JsonSerializer.SerializeToElement(new
            {
                cost
            }, JsonSerializerOptions.Web);
        }

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static Dictionary<string, object?> BuildOpenRouterVideoPayload(VideoRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        if (request.Seed is not null)
            payload["seed"] = request.Seed;

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

    private async Task<OpenRouterVideoPollResult> PollOpenRouterVideoAsync(
        string jobId,
        JsonElement createRoot,
        CancellationToken cancellationToken)
    {
        var pollPath = ReadOpenRouterVideoString(createRoot, "polling_url")
            ?? $"v1/videos/{Uri.EscapeDataString(jobId)}";

        using var pollReq = new HttpRequestMessage(HttpMethod.Get, pollPath);
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);

        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenRouter video poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var status = ReadOpenRouterVideoString(root, "status") ?? "unknown";

        return new OpenRouterVideoPollResult(status, pollRaw, root);
    }

    private async Task<List<VideoResponseFile>> DownloadOpenRouterVideosAsync(
        string jobId,
        JsonElement completedRoot,
        CancellationToken cancellationToken)
    {
        var outputCount = GetOpenRouterUnsignedUrlCount(completedRoot);
        if (outputCount <= 0)
            outputCount = 1;

        List<VideoResponseFile> videos = [];
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

            videos.Add(new VideoResponseFile
            {
                MediaType = mediaType,
                Data = Convert.ToBase64String(bytes)
            });
        }

        return videos;
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

    private static bool IsOpenRouterVideoTerminalStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.Equals("completed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
               || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
               || status.Equals("expired", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOpenRouterVideoSuccessStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.Equals("completed", StringComparison.OrdinalIgnoreCase);
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
