using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AgnesAI;

public partial class AgnesAIProvider
{
    private sealed record AgnesVideoTaskStatus(string Status, JsonElement Root);

    private static readonly JsonSerializerOptions AgnesJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static Dictionary<string, object?> CreateAgnesPayload(JsonElement metadata, params string[] excludedProperties)
    {
        var excluded = excludedProperties.Length == 0
            ? []
            : excludedProperties.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return CreateAgnesObject(metadata, excluded);
    }

    private static Dictionary<string, object?> CreateAgnesExtraBody(JsonElement metadata, params string[] excludedProperties)
    {
        var excluded = excludedProperties.Length == 0
            ? []
            : excludedProperties.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var propertyName in new[] { "extra_body", "extraBody" })
        {
            if (metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty(propertyName, out var extraBody)
                && extraBody.ValueKind == JsonValueKind.Object)
            {
                return CreateAgnesObject(extraBody, excluded);
            }
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> CreateAgnesObject(JsonElement element, HashSet<string> excluded)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (element.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in element.EnumerateObject())
        {
            if (excluded.Contains(property.Name))
                continue;

            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    private static List<string> ResolveAgnesImageInputUrls(ImageRequest request, JsonElement metadata, List<object> warnings)
    {
        var urls = new List<string>();
        var unsupportedLocalFiles = 0;

        foreach (var file in request.Files ?? [])
        {
            if (LooksLikeHttpUrl(file.Data))
                urls.Add(file.Data.Trim());
            else
                unsupportedLocalFiles++;
        }

        urls.AddRange(ReadAgnesConfiguredImageUrls(metadata));
        var distinctUrls = DistinctAgnesUrls(urls);

        if (unsupportedLocalFiles > 0 && distinctUrls.Count == 0)
        {
            throw new ArgumentException(
                "Agnes image editing requires public image URLs via providerOptions.agnesai.extra_body.image or providerOptions.agnesai.image_urls; raw file uploads are not supported.",
                nameof(request));
        }

        if (unsupportedLocalFiles > 0)
        {
            warnings.Add(new
            {
                type = "ignored",
                feature = "files",
                details = "Agnes image editing accepts public image URLs only; local file uploads were ignored in favor of supplied Agnes image URLs."
            });
        }

        return distinctUrls;
    }

    private static List<string> ResolveAgnesVideoInputUrls(VideoRequest request, JsonElement metadata, List<object> warnings)
    {
        var urls = new List<string>();
        var unsupportedLocalImage = false;

        if (request.Image is not null)
        {
            if (LooksLikeHttpUrl(request.Image.Data))
                urls.Add(request.Image.Data.Trim());
            else
                unsupportedLocalImage = true;
        }

        urls.AddRange(ReadAgnesConfiguredImageUrls(metadata));
        var distinctUrls = DistinctAgnesUrls(urls);

        if (unsupportedLocalImage && distinctUrls.Count == 0)
        {
            throw new ArgumentException(
                "Agnes video image inputs require public image URLs via providerOptions.agnesai.image_url, providerOptions.agnesai.image_urls, or providerOptions.agnesai.extra_body.image; raw file uploads are not supported.",
                nameof(request));
        }

        if (unsupportedLocalImage)
        {
            warnings.Add(new
            {
                type = "ignored",
                feature = "image",
                details = "Agnes video inputs accept public image URLs only; the local image upload was ignored in favor of supplied Agnes image URLs."
            });
        }

        return distinctUrls;
    }

    private static List<string> ReadAgnesConfiguredImageUrls(JsonElement metadata)
    {
        var urls = new List<string>();

        foreach (var propertyName in new[] { "extra_body", "extraBody" })
        {
            if (metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty(propertyName, out var extraBody)
                && extraBody.ValueKind == JsonValueKind.Object)
            {
                urls.AddRange(ReadStringList(extraBody, "image", "images", "image_urls", "imageUrls"));
            }
        }

        urls.AddRange(ReadStringList(metadata, "image", "images", "image_url", "imageUrl", "image_urls", "imageUrls"));
        return urls;
    }

    private static List<string> ResolveAgnesTags(JsonElement metadata, bool includeImg2Img)
    {
        var tags = ReadStringList(metadata, "tags");

        if (includeImg2Img && !tags.Contains("img2img", StringComparer.OrdinalIgnoreCase))
            tags.Add("img2img");

        return tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveAgnesImageResponseFormat(JsonElement metadata, List<object> warnings)
    {
        var requested = ReadNestedString(metadata, new[] { "extra_body", "extraBody" }, "response_format", "responseFormat")
            ?? ReadString(metadata, "response_format", "responseFormat");

        if (!string.IsNullOrWhiteSpace(requested)
            && !string.Equals(requested, "url", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "response_format",
                details = $"Agnes currently documents response_format=url; requested '{requested}' was replaced with 'url'."
            });
        }

        return "url";
    }

    private static string? ResolveAgnesImageSize(ImageRequest request, JsonElement metadata, List<object> warnings)
    {
        if (!string.IsNullOrWhiteSpace(request.Size))
            return request.Size;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            var inferred = request.AspectRatio.InferSizeFromAspectRatio(
                minWidth: 256,
                maxWidth: 1536,
                minHeight: 256,
                maxHeight: 1536);

            if (inferred is { } size)
            {
                var value = $"{size.width}x{size.height}";
                warnings.Add(new { type = "mapped_property", property = "aspectRatio", mappedTo = "size", value });
                return value;
            }
        }

        return ReadString(metadata, "size");
    }

    private static (int width, int height)? ResolveAgnesVideoSize(VideoRequest request, JsonElement metadata, List<object> warnings)
    {
        if (TryParseSize(request.Resolution, out var width, out var height))
            return (width, height);

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            var metadataWidth = ReadInt(metadata, "width");
            var metadataHeight = ReadInt(metadata, "height");

            if (metadataWidth is not null && metadataHeight is not null)
                return (metadataWidth.Value, metadataHeight.Value);

            if (TryParseSize(ReadString(metadata, "size", "resolution"), out width, out height))
                return (width, height);
        }

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            var inferred = request.AspectRatio.InferSizeFromAspectRatio(
                minWidth: 256,
                maxWidth: 1536,
                minHeight: 256,
                maxHeight: 1536);

            if (inferred is { } size)
            {
                warnings.Add(new
                {
                    type = "mapped_property",
                    property = "aspectRatio",
                    mappedTo = "width/height",
                    value = $"{size.width}x{size.height}"
                });

                return size;
            }
        }

        return null;
    }

    private static string? ResolveAgnesVideoMode(JsonElement metadata)
        => ReadNestedString(metadata, new[] { "extra_body", "extraBody" }, "mode")
            ?? ReadString(metadata, "mode");

    private static int ResolveAgnesPollIntervalSeconds(JsonElement metadata)
        => ReadInt(metadata, "poll_interval_seconds", "pollIntervalSeconds") ?? 5;

    private static int ResolveAgnesPollTimeoutMinutes(JsonElement metadata)
        => ReadInt(metadata, "poll_timeout_minutes", "pollTimeoutMinutes") ?? 10;

    private static int? ResolveAgnesPollMaxAttempts(JsonElement metadata)
        => ReadInt(metadata, "poll_max_attempts", "pollMaxAttempts");

    private static bool AgnesVideoStatusIsTerminal(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAgnesVideoError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return "Unknown error";

        if (root.TryGetProperty("error", out var error))
            return error.ValueKind == JsonValueKind.String ? error.GetString() ?? "Unknown error" : error.GetRawText();

        if (root.TryGetProperty("message", out var message))
            return message.ValueKind == JsonValueKind.String ? message.GetString() ?? "Unknown error" : message.GetRawText();

        return "Unknown error";
    }

    private static List<string> ExtractAgnesImageOutputUrls(JsonElement root)
    {
        var urls = new List<string>();

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return urls;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var url = item.TryGetString("url");
            if (LooksLikeHttpUrl(url))
                urls.Add(url!);
        }

        return DistinctAgnesUrls(urls);
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadAgnesBinaryAsync(string url, string defaultMediaType, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var text = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Agnes media download failed ({(int)response.StatusCode}): {text}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
            mediaType = defaultMediaType;

        return (bytes, mediaType!);
    }

    private static bool TryParseSize(string? value, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().Replace(':', 'x').ToLowerInvariant();
        var parts = normalized.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;

        return int.TryParse(parts[0], out width)
            && int.TryParse(parts[1], out height)
            && width > 0
            && height > 0;
    }

    private static List<string> DistinctAgnesUrls(IEnumerable<string> urls)
        => urls
            .Where(url => LooksLikeHttpUrl(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool LooksLikeHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static List<string> ReadStringList(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
                continue;

            return value.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? [] : [value.GetString()!],
                JsonValueKind.Array =>
                [
                    .. value
                        .EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString())
                        .OfType<string>()
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                ],
                _ => []
            };
        }

        return [];
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static string? ReadNestedString(JsonElement element, IEnumerable<string> parentNames, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var parentName in parentNames)
        {
            if (element.TryGetProperty(parentName, out var parent) && parent.ValueKind == JsonValueKind.Object)
            {
                var value = ReadString(parent, names);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
        }

        return null;
    }

    private static string? GuessAgnesImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var lower = url.Trim().ToLowerInvariant();
        if (lower.Contains(".png")) return MediaTypeNames.Image.Png;
        if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return MediaTypeNames.Image.Jpeg;
        if (lower.Contains(".gif")) return MediaTypeNames.Image.Gif;
        if (lower.Contains(".bmp")) return "image/bmp";
        if (lower.Contains(".webp")) return "image/webp";
        if (lower.Contains(".avif")) return "image/avif";

        return null;
    }

    private static string? GuessAgnesVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var lower = url.Trim().ToLowerInvariant();
        if (lower.Contains(".mp4")) return "video/mp4";
        if (lower.Contains(".webm")) return "video/webm";
        if (lower.Contains(".mov")) return "video/quicktime";
        if (lower.Contains(".mkv")) return "video/x-matroska";
        if (lower.Contains(".avi")) return "video/x-msvideo";

        return null;
    }
}
