using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Infron;

public partial class InfronProvider
{
    private static readonly Uri InfronImageGenerationsUri = new("https://image.onerouter.pro/v1/images/generations");
    private static readonly Uri InfronImageEditsUri = new("https://image.onerouter.pro/v1/images/edits");

    private static readonly JsonSerializerOptions InfronImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ImageResponse> InfronImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var hasFiles = request.Files?.Any() == true;
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildInfronImagePayload(request, hasFiles, metadata, warnings);
        var json = JsonSerializer.Serialize(payload, InfronImageJsonOptions);
        var endpoint = hasFiles ? InfronImageEditsUri : InfronImageGenerationsUri;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var httpResponse = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Infron image request failed ({(int)httpResponse.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var images = await ExtractInfronImagesAsync(root, cancellationToken);

        if (images.Count == 0)
            throw new InvalidOperationException("No valid images returned from Infron image API.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ExtractInfronImageUsage(root),
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root
            },
            Response = new()
            {
                Timestamp = ResolveInfronImageTimestamp(root, now),
                ModelId = root.TryGetString("model")?.ToModelId(GetIdentifier()) ?? request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    internal static Dictionary<string, object?> BuildInfronImagePayload(
        ImageRequest request,
        bool hasFiles,
        JsonElement? metadata = null,
        List<object>? warnings = null)
    {
        var outputFormat = metadata?.TryGetString("output_format")
            ?? metadata?.TryGetString("outputFormat")
            ?? metadata?.TryGetString("response_format")
            ?? metadata?.TryGetString("responseFormat")
            ?? "url";

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = request.N,
            ["size"] = request.Size,
            ["output_format"] = outputFormat
        };

        if (request.Seed is not null)
            payload["seed"] = request.Seed;

        AddInfronImageMetadataPassthrough(payload, metadata);

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings?.Add(new
            {
                type = "unsupported",
                feature = "aspectRatio",
                details = "Infron image endpoints are OpenAI-compatible and use the size field for output dimensions."
            });
        }

        if (request.Mask is not null)
        {
            warnings?.Add(new
            {
                type = "unsupported",
                feature = "mask",
                details = "Infron image editing documentation accepts image URLs/data URLs and does not document a separate mask parameter."
            });
        }

        if (hasFiles)
        {
            var imageUrls = request.Files!
                .Select(ToInfronImageInput)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            if (imageUrls.Length == 0)
                throw new ArgumentException("At least one non-empty image input is required for Infron image editing.", nameof(request));

            payload["image_urls"] = imageUrls;
        }

        return payload;
    }

    private static void AddInfronImageMetadataPassthrough(Dictionary<string, object?> payload, JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } metadataElement)
            return;

        foreach (var property in metadataElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "outputFormat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "responseFormat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "image_urls", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "imageUrls", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            payload[property.Name] = property.Value.Clone();
        }
    }

    private async Task<List<string>> ExtractInfronImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataEl.EnumerateArray())
            {
                var image = await NormalizeInfronImageItemAsync(item, cancellationToken);

                if (!string.IsNullOrWhiteSpace(image))
                    images.Add(image);
            }
        }

        return images;
    }

    private async Task<string?> NormalizeInfronImageItemAsync(JsonElement item, CancellationToken cancellationToken)
    {
        var b64 = item.TryGetString("b64_json")
            ?? item.TryGetString("base64")
            ?? item.TryGetString("data");

        if (!string.IsNullOrWhiteSpace(b64))
            return b64.ToDataUrl(GuessInfronImageMediaType(item) ?? MediaTypeNames.Image.Png);

        var url = item.TryGetString("url")
            ?? item.TryGetString("image_url")
            ?? item.TryGetString("imageUrl");

        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return url;

        var bytes = await _client.GetByteArrayAsync(url, cancellationToken);
        var mediaType = GuessInfronImageMediaType(item)
            ?? GuessInfronImageMediaTypeFromUrl(url)
            ?? MediaTypeNames.Image.Png;

        return Convert.ToBase64String(bytes).ToDataUrl(mediaType);
    }

    private static string ToInfronImageInput(ImageFile file)
    {
        if (string.Equals(file.Type, "url", StringComparison.OrdinalIgnoreCase)
            || Uri.TryCreate(file.Data, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Image.Png
            : file.MediaType;

        return file.Data.ToDataUrl(mediaType);
    }

    private static ImageUsageData? ExtractInfronImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
            return null;

        return new ImageUsageData
        {
            InputTokens = TryReadInfronImageInt(usageEl, "input_tokens") ?? TryReadInfronImageInt(usageEl, "inputTokens"),
            OutputTokens = TryReadInfronImageInt(usageEl, "output_tokens") ?? TryReadInfronImageInt(usageEl, "outputTokens"),
            TotalTokens = TryReadInfronImageInt(usageEl, "total_tokens") ?? TryReadInfronImageInt(usageEl, "totalTokens")
        };
    }

    private static int? TryReadInfronImageInt(JsonElement element, string propertyName)
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

    private static DateTime ResolveInfronImageTimestamp(JsonElement root, DateTime fallback)
    {
        if (root.TryGetProperty("created", out var createdEl))
        {
            long? unix = createdEl.ValueKind switch
            {
                JsonValueKind.Number when createdEl.TryGetInt64(out var number) => number,
                JsonValueKind.String when long.TryParse(createdEl.GetString(), out var parsed) => parsed,
                _ => null
            };

            if (unix.HasValue)
                return DateTimeOffset.FromUnixTimeSeconds(unix.Value).UtcDateTime;
        }

        return fallback;
    }

    private static string? GuessInfronImageMediaType(JsonElement item)
    {
        var value = item.TryGetString("mime_type")
            ?? item.TryGetString("mimeType")
            ?? item.TryGetString("content_type")
            ?? item.TryGetString("contentType")
            ?? item.TryGetString("format")
            ?? item.TryGetString("output_format");

        return NormalizeInfronImageMediaType(value);
    }

    private static string? GuessInfronImageMediaTypeFromUrl(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : url;

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => MediaTypeNames.Image.Png,
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".webp" => "image/webp",
            ".gif" => MediaTypeNames.Image.Gif,
            ".bmp" => "image/bmp",
            _ => null
        };
    }

    private static string? NormalizeInfronImageMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "png" => MediaTypeNames.Image.Png,
            "jpg" or "jpeg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            "gif" => MediaTypeNames.Image.Gif,
            "bmp" => "image/bmp",
            var mime when mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => mime,
            _ => null
        };
    }
}
