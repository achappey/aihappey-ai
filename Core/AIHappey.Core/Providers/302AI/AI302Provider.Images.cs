using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.AI302;

public partial class AI302Provider
{
    private static readonly JsonSerializerOptions AI302ImageJsonOptions = new(JsonSerializerDefaults.Web)
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
        var warnings = new List<object>();

        if (request.N is not null && request.N.Value > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "302.AI synchronous image generation returns a single task result. Requested n>1 is ignored."
            });
        }

        if (request.Seed.HasValue)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed"
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };

        var (width, height) = ParseImageSize(request.Size);
        if (width.HasValue)
            payload["width"] = width.Value;
        if (height.HasValue)
            payload["height"] = height.Value;

        if (!string.IsNullOrEmpty(request.AspectRatio))
        {
            payload["aspect_ratio"] = request.AspectRatio;
        }

        if (request.Files?.Any() == true)
        {
            var first = request.Files.First();
            payload["image"] = EnsureImageInput(first.Data, first.MediaType);

            if (request.Files.Skip(1).Any())
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "files",
                    details = "302.AI image input currently maps only the first file to 'image'."
                });
            }
        }

        if (request.Mask is not null)
            payload["mask_image"] = EnsureImageInput(request.Mask.Data, request.Mask.MediaType);

        var json = JsonSerializer.Serialize(payload, AI302ImageJsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "302/v2/image/generate")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"302.AI image generation failed ({(int)response.StatusCode})."
                : $"302.AI image generation failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var urls = ExtractImageUrls(root);
        if (urls.Count == 0)
            throw new InvalidOperationException("302.AI image generation returned no image URLs.");

        var images = new List<string>();
        foreach (var url in urls)
        {
            if (url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            {
                images.Add(url);
                continue;
            }

            using var imgResp = await _client.GetAsync(url, cancellationToken);
            var bytes = await imgResp.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!imgResp.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"Failed to download 302.AI image from returned URL ({(int)imgResp.StatusCode}).");

            var mediaType = imgResp.Content.Headers.ContentType?.MediaType
                ?? GuessImageMediaType(url)
                ?? MediaTypeNames.Image.Png;

            images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
        }

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

    private static List<string> ExtractImageUrls(JsonElement root)
    {
        var results = new List<string>();

        if (root.TryGetProperty("image_urls", out var imageUrlsEl) && imageUrlsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in imageUrlsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;

                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    results.Add(value);
            }
        }

        if (results.Count == 0 && root.TryGetProperty("image_url", out var imageUrlEl) && imageUrlEl.ValueKind == JsonValueKind.String)
        {
            var single = imageUrlEl.GetString();
            if (!string.IsNullOrWhiteSpace(single))
                results.Add(single);
        }

        return results;
    }

    private static (int? width, int? height) ParseImageSize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size))
            return (null, null);

        var normalized = size.Trim().Replace("*", "x", StringComparison.OrdinalIgnoreCase);
        var parts = normalized.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 2)
            return (null, null);

        if (!int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height))
            return (null, null);

        return (width, height);
    }

    private static string EnsureImageInput(string data, string mediaType)
    {
        if (string.IsNullOrWhiteSpace(data))
            return data;

        if (data.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
            data.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            return data;

        return data.ToDataUrl(mediaType);
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
