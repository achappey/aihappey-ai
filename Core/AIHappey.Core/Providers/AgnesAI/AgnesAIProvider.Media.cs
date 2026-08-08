using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AgnesAI;

public partial class AgnesAIProvider
{
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
        var inputs = new List<string>();

        foreach (var file in request.Files ?? [])
        {
            if (string.IsNullOrWhiteSpace(file.Data))
                throw new ArgumentException("Agnes image input data is required.", nameof(request));

            var data = file.Data.Trim();
            if (LooksLikeHttpUrl(data) || LooksLikeDataUri(data))
                inputs.Add(data);
            else if (!string.IsNullOrWhiteSpace(file.MediaType))
                inputs.Add(data.ToDataUrl(file.MediaType));
            else
                throw new ArgumentException("Agnes raw image input requires a media type.", nameof(request));
        }

        inputs.AddRange(ReadAgnesConfiguredImageUrls(metadata));
        return DistinctAgnesImageInputs(inputs);
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

    private static string? ResolveAgnesImageRatio(ImageRequest request, JsonElement metadata)
        => !string.IsNullOrWhiteSpace(request.AspectRatio)
            ? request.AspectRatio
            : ReadString(metadata, "ratio", "aspect_ratio", "aspectRatio");

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

    private static List<AgnesImageOutput> ExtractAgnesImageOutputs(JsonElement root)
    {
        var outputs = new List<AgnesImageOutput>();

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return outputs;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var base64 = item.TryGetString("b64_json");
            var url = item.TryGetString("url");
            if (!string.IsNullOrWhiteSpace(base64))
                outputs.Add(new AgnesImageOutput(base64, null));
            else if (LooksLikeHttpUrl(url))
                outputs.Add(new AgnesImageOutput(null, url));
        }

        return outputs;
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

    private static List<string> DistinctAgnesImageInputs(IEnumerable<string> inputs)
        => inputs
            .Where(input => LooksLikeHttpUrl(input) || LooksLikeDataUri(input))
            .Select(input => input.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static bool LooksLikeHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool LooksLikeDataUri(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);

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

    private sealed record AgnesImageOutput(string? Base64, string? Url);
}
