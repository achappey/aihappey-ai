using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.ImageRouter;

public partial class ImageRouterProvider
{
    private static readonly JsonSerializerOptions ImageRouterJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> TerminalStatuses =
    [
        "completed",
        "complete",
        "success",
        "succeeded",
        "failed",
        "error",
        "cancelled",
        "canceled"
    ];

    private static readonly HashSet<string> NonTerminalStatuses =
    [
        "queued",
        "pending",
        "processing",
        "running",
        "in_progress",
        "in-progress"
    ];

    private static bool HasImageUploads(ImageRequest request)
        => (request.Files?.Any() ?? false) || request.Mask is not null;

    private static string ResolveBase64ResponseFormat(Dictionary<string, JsonElement>? providerOptions)
    {
        if (TryGetProviderString(providerOptions, "response_format", out var responseFormat)
            && string.Equals(responseFormat, "b64_ephemeral", StringComparison.OrdinalIgnoreCase))
            return "b64_ephemeral";

        return "b64_json";
    }

    private static bool TryGetProviderString(Dictionary<string, JsonElement>? providerOptions, string propertyName, out string? value)
    {
        value = null;

        if (providerOptions is null)
            return false;

        if (providerOptions.TryGetValue(nameof(ImageRouter).ToLowerInvariant(), out var providerValue)
            && providerValue.ValueKind == JsonValueKind.Object
            && providerValue.TryGetProperty(propertyName, out var nestedValue)
            && nestedValue.ValueKind == JsonValueKind.String)
        {
            value = nestedValue.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        if (providerOptions.TryGetValue(propertyName, out var rootValue)
            && rootValue.ValueKind == JsonValueKind.String)
        {
            value = rootValue.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    private static void MergeRawProviderOptions(Dictionary<string, object?> payload, Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null || providerOptions.Count == 0)
            return;

        if (providerOptions.TryGetValue(nameof(ImageRouter).ToLowerInvariant(), out var imageRouterOptions)
            && imageRouterOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in imageRouterOptions.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        foreach (var option in providerOptions)
        {
            if (string.Equals(option.Key, nameof(ImageRouter).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                continue;

            payload[option.Key] = option.Value.Clone();
        }
    }

    private static void AddMultipartValue(MultipartFormDataContent multipart, string key, object? value)
    {
        if (value is null)
            return;

        if (value is JsonElement element)
        {
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return;

            if (element.ValueKind == JsonValueKind.String)
            {
                var stringValue = element.GetString();
                if (!string.IsNullOrWhiteSpace(stringValue))
                    multipart.Add(new StringContent(stringValue), key);

                return;
            }

            multipart.Add(new StringContent(element.GetRawText()), key);
            return;
        }

        switch (value)
        {
            case string s when !string.IsNullOrWhiteSpace(s):
                multipart.Add(new StringContent(s), key);
                break;

            case bool b:
                multipart.Add(new StringContent(b ? "true" : "false"), key);
                break;

            default:
                multipart.Add(new StringContent(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty), key);
                break;
        }
    }

    private static ByteArrayContent CreateFileContent(ImageFile file)
    {
        var bytes = Convert.FromBase64String(file.Data.RemoveDataUrlPrefix());
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.MediaType);
        return content;
    }

    private static ByteArrayContent CreateFileContent(VideoFile file)
    {
        var bytes = Convert.FromBase64String(file.Data.RemoveDataUrlPrefix());
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.MediaType);
        return content;
    }

    private static string GetFileName(string mediaType, string prefix)
    {
        var extension = mediaType.ToLowerInvariant() switch
        {
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/webp" => "webp",
            "image/gif" => "gif",
            "video/mp4" => "mp4",
            "video/webm" => "webm",
            "video/quicktime" => "mov",
            _ => "bin"
        };

        return $"{prefix}.{extension}";
    }

    private async Task<JsonElement> EnsureTerminalImageRouterResponseAsync(JsonElement initial, CancellationToken cancellationToken)
    {
        if (IsImageRouterTerminal(initial))
            return initial;

        var pollUrl = GetPollUrl(initial);
        if (string.IsNullOrWhiteSpace(pollUrl))
            return initial;

        return await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: async ct => await FetchImageRouterPollResponseAsync(pollUrl, ct),
            isTerminal: IsImageRouterTerminal,
            interval: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);
    }

    private async Task<JsonElement> FetchImageRouterPollResponseAsync(string pollUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pollUrl);
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"ImageRouter polling error: {(int)response.StatusCode} {response.StatusCode}: {raw}");

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static bool IsImageRouterTerminal(JsonElement root)
    {
        if (TryGetStatus(root, out var status))
        {
            if (TerminalStatuses.Contains(status))
                return true;

            if (NonTerminalStatuses.Contains(status))
                return false;
        }

        return HasCompletedData(root);
    }

    private static bool TryGetStatus(JsonElement root, out string status)
    {
        status = string.Empty;

        if (root.TryGetProperty("status", out var statusElement)
            && statusElement.ValueKind == JsonValueKind.String)
        {
            status = statusElement.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(status);
        }

        return false;
    }

    private static bool HasCompletedData(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in dataElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (item.TryGetProperty("b64_json", out var b64Json) && b64Json.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(b64Json.GetString()))
                return true;

            if (item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(url.GetString()))
                return true;
        }

        return false;
    }

    private static string? GetPollUrl(JsonElement root)
    {
        if (root.TryGetProperty("fetch_result", out var fetchResult) && fetchResult.ValueKind == JsonValueKind.String)
            return fetchResult.GetString();

        if (root.TryGetProperty("result_url", out var resultUrl) && resultUrl.ValueKind == JsonValueKind.String)
            return resultUrl.GetString();

        return null;
    }

    private static void ThrowIfImageRouterError(JsonElement root, string operation)
    {
        if (TryGetStatus(root, out var status)
            && (status == "error" || status == "failed" || status == "cancelled" || status == "canceled"))
        {
            throw new Exception($"ImageRouter {operation} error: {GetErrorMessage(root)}");
        }

        if (root.TryGetProperty("error", out var errorElement))
            throw new Exception($"ImageRouter {operation} error: {GetErrorMessage(root)}");
    }

    private static string GetErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var errorElement))
        {
            if (errorElement.ValueKind == JsonValueKind.String)
                return errorElement.GetString() ?? "Unknown ImageRouter error.";

            if (errorElement.ValueKind == JsonValueKind.Object)
            {
                if (errorElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    return message.GetString() ?? "Unknown ImageRouter error.";

                return errorElement.GetRawText();
            }
        }

        if (root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
            return messageElement.GetString() ?? "Unknown ImageRouter error.";

        return "Unknown ImageRouter error.";
    }

    private async Task<List<string>> ExtractImageOutputsAsync(JsonElement root, List<object> warnings, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in dataElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (item.TryGetProperty("b64_json", out var b64Json) && b64Json.ValueKind == JsonValueKind.String)
            {
                var base64 = b64Json.GetString();
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    images.Add(base64.ToDataUrl(GuessImageMediaType(root, item) ?? MediaTypeNames.Image.Jpeg));
                    continue;
                }
            }

            if (item.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String)
            {
                var url = urlElement.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    images.Add(await DownloadImageAsDataUrlAsync(url, cancellationToken));
                    continue;
                }
            }
        }

        return images;
    }

    private async Task<List<VideoResponseFile>> ExtractVideoOutputsAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var videos = new List<VideoResponseFile>();

        if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
            return videos;

        foreach (var item in dataElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (item.TryGetProperty("b64_json", out var b64Json) && b64Json.ValueKind == JsonValueKind.String)
            {
                var base64 = b64Json.GetString();
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    videos.Add(new VideoResponseFile
                    {
                        Data = base64,
                        MediaType = GuessVideoMediaType(root, item) ?? "video/mp4"
                    });

                    continue;
                }
            }

            if (item.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String)
            {
                var url = urlElement.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    videos.Add(await DownloadVideoAsync(url, cancellationToken));
                    continue;
                }
            }
        }

        return videos;
    }

    private async Task<string> DownloadImageAsDataUrlAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"ImageRouter image download failed: {(int)response.StatusCode} {response.StatusCode}: {System.Text.Encoding.UTF8.GetString(bytes)}");

        var mediaType = response.Content.Headers.ContentType?.MediaType
            ?? GuessMediaTypeFromUrl(url)
            ?? MediaTypeNames.Image.Jpeg;

        return Convert.ToBase64String(bytes).ToDataUrl(mediaType);
    }

    private async Task<VideoResponseFile> DownloadVideoAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"ImageRouter video download failed: {(int)response.StatusCode} {response.StatusCode}: {System.Text.Encoding.UTF8.GetString(bytes)}");

        var mediaType = response.Content.Headers.ContentType?.MediaType
            ?? GuessMediaTypeFromUrl(url)
            ?? "video/mp4";

        return new VideoResponseFile
        {
            Data = Convert.ToBase64String(bytes),
            MediaType = mediaType
        };
    }

    private static Dictionary<string, JsonElement> BuildProviderMetadata(JsonElement root)
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            [nameof(ImageRouter).ToLowerInvariant()] = root.Clone()
        };

        if (root.TryGetProperty("latency", out var latency))
            metadata["latency"] = latency.Clone();

        if (root.TryGetProperty("cost", out var cost))
            metadata["cost"] = cost.Clone();

        if (root.TryGetProperty("created", out var created))
            metadata["created"] = created.Clone();

        return metadata;
    }

    private static string? GuessImageMediaType(JsonElement root, JsonElement item)
        => GetOutputFormat(root, item) switch
        {
            "png" => MediaTypeNames.Image.Png,
            "jpeg" => MediaTypeNames.Image.Jpeg,
            "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            "gif" => MediaTypeNames.Image.Gif,
            _ => null
        };

    private static string? GuessVideoMediaType(JsonElement root, JsonElement item)
        => GetOutputFormat(root, item) switch
        {
            "webm" => "video/webm",
            "mov" => "video/quicktime",
            "gif" => MediaTypeNames.Image.Gif,
            _ => "video/mp4"
        };

    private static string? GetOutputFormat(JsonElement root, JsonElement item)
    {
        if (item.TryGetProperty("output_format", out var itemFormat) && itemFormat.ValueKind == JsonValueKind.String)
            return itemFormat.GetString()?.Trim().ToLowerInvariant();

        if (root.TryGetProperty("output_format", out var rootFormat) && rootFormat.ValueKind == JsonValueKind.String)
            return rootFormat.GetString()?.Trim().ToLowerInvariant();

        return null;
    }

    private static string? GuessMediaTypeFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var withoutQuery = url.Split('?', '#')[0];

        if (withoutQuery.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Png;
        if (withoutQuery.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Jpeg;
        if (withoutQuery.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Jpeg;
        if (withoutQuery.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        if (withoutQuery.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Gif;
        if (withoutQuery.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)) return "image/bmp";
        if (withoutQuery.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return "image/svg+xml";
        if (withoutQuery.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return "video/mp4";
        if (withoutQuery.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) return "video/webm";
        if (withoutQuery.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)) return "video/quicktime";

        return null;
    }

    private static bool TryResolveAspectRatioSize(string? aspectRatio, int maxWidth, int maxHeight, out string? size)
    {
        size = null;

        if (string.IsNullOrWhiteSpace(aspectRatio))
            return false;

        var inferred = aspectRatio.InferSizeFromAspectRatio(maxWidth: maxWidth, maxHeight: maxHeight, minWidth: 256, minHeight: 256);
        if (!inferred.HasValue)
            return false;

        size = $"{inferred.Value.width}x{inferred.Value.height}";
        return true;
    }
}
