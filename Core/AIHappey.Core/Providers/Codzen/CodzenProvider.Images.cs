using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Codzen;

public partial class CodzenProvider
{
    private static readonly JsonSerializerOptions CodzenImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = BuildCodzenImageWarnings(request).ToList();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildCodzenImagePayload(request, metadata);
        var body = JsonSerializer.Serialize(payload, CodzenImageJsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations/")
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var httpResponse = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Codzen image generation failed ({(int)httpResponse.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("No image data returned from Codzen image API.");

        var images = new List<string>();

        foreach (var item in dataEl.EnumerateArray())
        {
            var image = await NormalizeCodzenImageItemAsync(item, cancellationToken);

            if (!string.IsNullOrWhiteSpace(image))
                images.Add(image);
        }

        if (images.Count == 0)
            throw new InvalidOperationException("No valid images returned from Codzen image API.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ReadCodzenImageUsage(root),
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = raw
            }
        };
    }

    private static Dictionary<string, object?> BuildCodzenImagePayload(ImageRequest request, JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = request.N,
            ["size"] = request.Size,
            ["background"] = metadata.TryGetString("background"),
            ["moderation"] = metadata.TryGetString("moderation"),
            ["quality"] = metadata.TryGetString("quality"),
            ["stream"] = metadata.TryGetString("stream"),
            ["style"] = metadata.TryGetString("style"),
            ["user"] = metadata.TryGetString("user")
        };

        return payload;
    }

    private static IEnumerable<object> BuildCodzenImageWarnings(ImageRequest request)
    {
        if (request.Files?.Any() == true)
        {
            yield return new
            {
                type = "unsupported",
                feature = "files",
                details = "Codzen image generation supports text-to-image requests. Input images were ignored."
            };
        }

        if (request.Mask is not null)
        {
            yield return new
            {
                type = "unsupported",
                feature = "mask"
            };
        }

        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
        {
            yield return new
            {
                type = "unsupported",
                feature = "aspectRatio",
                details = "Codzen follows the OpenAI Images API size field. Provide size when specific output dimensions are required."
            };
        }

        if (request.Seed is not null)
        {
            yield return new
            {
                type = "unsupported",
                feature = "seed"
            };
        }
    }

    private async Task<string?> NormalizeCodzenImageItemAsync(JsonElement item, CancellationToken cancellationToken)
    {
        var b64 = item.TryGetString("b64_json")
            ?? item.TryGetString("base64")
            ?? item.TryGetString("data");

        if (!string.IsNullOrWhiteSpace(b64))
            return b64.ToDataUrl(GuessCodzenImageMediaType(item) ?? MediaTypeNames.Image.Png);

        var url = item.TryGetString("url");
        if (string.IsNullOrWhiteSpace(url))
            return null;

        using var imageResponse = await _client.GetAsync(url, cancellationToken);
        if (!imageResponse.IsSuccessStatusCode)
            return null;

        var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (imageBytes.Length == 0)
            return null;

        var mediaType = imageResponse.Content.Headers.ContentType?.MediaType
            ?? GuessCodzenImageMediaType(item)
            ?? GuessCodzenImageMediaTypeFromUrl(url)
            ?? MediaTypeNames.Image.Png;

        return Convert.ToBase64String(imageBytes).ToDataUrl(mediaType);
    }

    private static ImageUsageData? ReadCodzenImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
            return null;

        return new ImageUsageData
        {
            InputTokens = ReadCodzenImageInt(usageEl, "input_tokens"),
            OutputTokens = ReadCodzenImageInt(usageEl, "output_tokens"),
            TotalTokens = ReadCodzenImageInt(usageEl, "total_tokens")
        };
    }

    private static int? ReadCodzenImageInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? GuessCodzenImageMediaType(JsonElement item)
        => item.TryGetString("mime_type")
            ?? item.TryGetString("mimeType")
            ?? item.TryGetString("content_type")
            ?? item.TryGetString("contentType")
            ?? item.TryGetString("format") switch
            {
                "png" => MediaTypeNames.Image.Png,
                "jpg" or "jpeg" => MediaTypeNames.Image.Jpeg,
                "gif" => MediaTypeNames.Image.Gif,
                "webp" => "image/webp",
                var value when !string.IsNullOrWhiteSpace(value) && value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => value,
                _ => null
            };

    private static string? GuessCodzenImageMediaTypeFromUrl(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : url;

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => MediaTypeNames.Image.Png,
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".gif" => MediaTypeNames.Image.Gif,
            ".webp" => "image/webp",
            _ => null
        };
    }
}
