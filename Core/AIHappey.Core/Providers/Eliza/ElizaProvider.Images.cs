using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Eliza;

public partial class ElizaProvider
{
    private const string ElizaDefaultImageModel = "google/gemini-2.5-flash-image";

    private static readonly JsonSerializerOptions ElizaImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var model = string.IsNullOrWhiteSpace(request.Model)
            ? ElizaDefaultImageModel
            : request.Model.Trim();
        var aspectRatio = ResolveElizaAspectRatio(request, metadata, warnings);
        var numImages = ResolveElizaNumImages(request.N, metadata, warnings);
        var sourceImage = ResolveElizaSourceImage(request, metadata, warnings);
        var stylePreset = metadata.TryGetString("stylePreset")
            ?? metadata.TryGetString("style_preset");

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (!string.IsNullOrWhiteSpace(request.Size))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "size",
                details = "Eliza image generation uses aspectRatio. Requested size was ignored."
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["model"] = model,
            ["aspectRatio"] = aspectRatio,
            ["numImages"] = numImages,
            ["stylePreset"] = string.IsNullOrWhiteSpace(stylePreset) ? null : stylePreset,
            ["sourceImage"] = sourceImage
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/generate-image")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ElizaImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Eliza image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = await ExtractElizaImagesAsync(root, cancellationToken);

        if (images.Count == 0)
            throw new InvalidOperationException("Eliza image generation returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = root.TryGetString("model")?.ToModelId(GetIdentifier()) ?? model.ToModelId(GetIdentifier())
            }
        };
    }

    private static string ResolveElizaAspectRatio(ImageRequest request, JsonElement metadata, List<object> warnings)
    {
        var aspectRatio = request.AspectRatio
            ?? metadata.TryGetString("aspectRatio")
            ?? metadata.TryGetString("aspect_ratio")
            ?? "1:1";

        return IsSupportedElizaAspectRatio(aspectRatio)
            ? aspectRatio
            : AddUnsupportedAspectRatioWarning(aspectRatio, warnings);
    }

    private static string AddUnsupportedAspectRatioWarning(string aspectRatio, List<object> warnings)
    {
        warnings.Add(new
        {
            type = "unsupported",
            feature = "aspectRatio",
            details = $"Eliza supports 1:1, 16:9, 9:16, 4:3, 3:4, 21:9, and 9:21. Requested {aspectRatio} was replaced with 1:1."
        });

        return "1:1";
    }

    private static bool IsSupportedElizaAspectRatio(string aspectRatio)
        => aspectRatio is "1:1" or "16:9" or "9:16" or "4:3" or "3:4" or "21:9" or "9:21";

    private static int ResolveElizaNumImages(int? n, JsonElement metadata, List<object> warnings)
    {
        var requested = n ?? TryGetElizaInt(metadata, "numImages") ?? TryGetElizaInt(metadata, "num_images") ?? 1;
        var clamped = Math.Clamp(requested, 1, 4);

        if (requested != clamped)
        {
            warnings.Add(new
            {
                type = "adjusted",
                feature = "numImages",
                requested,
                actual = clamped,
                details = "Eliza supports between 1 and 4 images per request."
            });
        }

        return clamped;
    }

    private static string? ResolveElizaSourceImage(ImageRequest request, JsonElement metadata, List<object> warnings)
    {
        var explicitSourceImage = metadata.TryGetString("sourceImage")
            ?? metadata.TryGetString("source_image");
        if (!string.IsNullOrWhiteSpace(explicitSourceImage))
            return explicitSourceImage;

        var files = request.Files?.ToList() ?? [];
        if (files.Count == 0)
            return null;

        if (files.Count > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Eliza image-to-image generation accepts one sourceImage. Extra files were ignored."
            });
        }

        var source = files[0];
        return source.ToDataUrl();
    }

    private static async Task<List<string>> ExtractElizaImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("images", out var imagesElement) || imagesElement.ValueKind != JsonValueKind.Array)
            return [];

        var images = new List<string>();

        foreach (var item in imagesElement.EnumerateArray())
        {
            var mimeType = GuessElizaImageMediaType(item);
            var base64 = item.TryGetString("image")
                ?? item.TryGetString("b64_json")
                ?? item.TryGetString("base64")
                ?? item.TryGetString("data");

            if (!string.IsNullOrWhiteSpace(base64))
            {
                images.Add(base64.ToDataUrl(mimeType));
                continue;
            }

            var url = item.TryGetString("url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            images.Add(await DownloadElizaImageAsDataUrlAsync(url, mimeType, cancellationToken));
        }

        return images;
    }

    private static async Task<string> DownloadElizaImageAsDataUrlAsync(
        string url,
        string fallbackMimeType,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        return Convert.ToBase64String(bytes).ToDataUrl(
            string.IsNullOrWhiteSpace(mediaType) ? fallbackMimeType : mediaType);
    }

    private static string GuessElizaImageMediaType(JsonElement item)
    {
        var value = item.TryGetString("mimeType")
            ?? item.TryGetString("mime_type")
            ?? item.TryGetString("contentType")
            ?? item.TryGetString("content_type")
            ?? item.TryGetString("format");

        if (string.IsNullOrWhiteSpace(value))
            return "image/webp";

        if (value.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return value;

        return value.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => MediaTypeNames.Image.Jpeg,
            "png" => MediaTypeNames.Image.Png,
            "gif" => MediaTypeNames.Image.Gif,
            "webp" => "image/webp",
            _ => "image/webp"
        };
    }

    private static int? TryGetElizaInt(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
                return number;
        }

        return null;
    }
}
