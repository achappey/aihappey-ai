using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.HorayAI;

public partial class HorayAIProvider
{
    private static readonly JsonSerializerOptions HorayImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> SupportedImageSizes =
    [
        "1024x1024",
        "1536x1536",
        "768x512",
        "768x1024",
        "1024x576",
        "576x1024"
    ];

    private async Task<ImageResponse> CreateImageAsync(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });

        var size = string.IsNullOrWhiteSpace(request.Size) ? "1024x1024" : request.Size.Trim();
        if (!SupportedImageSizes.Contains(size))
        {
            warnings.Add(new { type = "unsupported", feature = "size", details = $"Unsupported size '{request.Size}', using 1024x1024." });
            size = "1024x1024";
        }

        var payload = new
        {
            model = request.Model,
            prompt = request.Prompt,
            image_size = size,
            seed = request.Seed
        };

        var json = JsonSerializer.Serialize(payload, HorayImageJsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/image/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"HorayAI image generation failed ({(int)resp.StatusCode})."
                : $"HorayAI image generation failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("images", out var imagesEl) || imagesEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("HorayAI image generation returned no images array.");

        var images = new List<string>();
        foreach (var image in imagesEl.EnumerateArray())
        {
            if (!image.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;

            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            using var imageResp = await _client.GetAsync(url, cancellationToken);
            var imageBytes = await imageResp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!imageResp.IsSuccessStatusCode || imageBytes.Length == 0)
                throw new InvalidOperationException($"Failed to download HorayAI image from returned URL ({(int)imageResp.StatusCode}).");

            var mediaType = imageResp.Content.Headers.ContentType?.MediaType
                ?? GuessImageMediaType(url)
                ?? MediaTypeNames.Image.Png;

            images.Add(Convert.ToBase64String(imageBytes).ToDataUrl(mediaType));
        }

        if (images.Count == 0)
            throw new InvalidOperationException("HorayAI image generation returned no downloadable images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static string? GuessImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.Contains(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";
        if (url.Contains(".jpg", StringComparison.OrdinalIgnoreCase) || url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase))
            return "image/jpeg";
        if (url.Contains(".png", StringComparison.OrdinalIgnoreCase))
            return "image/png";

        return null;
    }
}
