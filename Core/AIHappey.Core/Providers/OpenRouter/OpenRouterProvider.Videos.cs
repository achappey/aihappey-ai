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

    private async Task<VideoResponse> VideoRequestOpenRouter(VideoRequest request, CancellationToken cancellationToken = default)
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

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    create = createRoot,
                    poll = completed.Root
                }, OpenRouterVideoJsonOptions)
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = completed.Root
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

        if (request.Image is not null)
        {
            payload["input_references"] = new[]
            {
                new
                {
                    image_url = new
                    {
                        url = NormalizeOpenRouterImageUrl(request.Image)
                    },
                    type = "image_url"
                }
            };
        }

        MergeOpenRouterVideoProviderOptions(payload, request);

        return payload;
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
        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Image.Png
            : file.MediaType;

        return $"data:{mediaType};base64,{file.Data}";
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
