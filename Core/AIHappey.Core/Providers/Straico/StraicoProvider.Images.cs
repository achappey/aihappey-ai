using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Straico;

public partial class StraicoProvider
{
    private static readonly JsonSerializerOptions StraicoImageJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> StraicoReservedImageFields =
    [
        "model",
        "description",
        "size",
        "variations"
    ];

    private async Task<ImageResponse> ImageRequestCore(ImageRequest request, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask",
                details = "Straico /v1/image/generation does not accept mask in this provider route. Ignored mask."
            });
        }

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aspect_ratio",
                details = "Straico expects size values such as square, landscape, portrait (and model-specific values). Ignored aspect_ratio."
            });
        }

        if (request.Seed.HasValue)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed",
                details = "Generic seed is not mapped by default in this route. Provide providerOptions.straico.seed when supported by the target model."
            });
        }

        if (request.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Image file inputs are not auto-hosted for Straico URL-only image context fields. Ignored files. Use providerOptions.straico.image_urls or image_url with public URLs."
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["description"] = request.Prompt,
            ["size"] = string.IsNullOrWhiteSpace(request.Size) ? "square" : request.Size,
            ["variations"] = request.N is > 0 ? request.N.Value : 1
        };

        if (request.ProviderOptions is not null &&
            request.ProviderOptions.TryGetValue(GetIdentifier(), out var straicoOptions) &&
            straicoOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in straicoOptions.EnumerateObject())
            {
                if (StraicoReservedImageFields.Contains(property.Name))
                    continue;

                payload[property.Name] = property.Value.Clone();
            }
        }

        var json = JsonSerializer.Serialize(payload, StraicoImageJsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/image/generation")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Straico API error: {(int)response.StatusCode} {response.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var images = await ParseStraicoImagesAsync(root, cancellationToken);
        if (images.Count == 0)
            throw new Exception("Straico image generation returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private async Task<List<string>> ParseStraicoImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
            return images;

        if (!dataEl.TryGetProperty("images", out var imageArrayEl) || imageArrayEl.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in imageArrayEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var url = item.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            using var imageResponse = await _client.GetAsync(url, cancellationToken);
            var bytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!imageResponse.IsSuccessStatusCode || bytes is null || bytes.Length == 0)
                continue;

            var mediaType = imageResponse.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mediaType = MediaTypeNames.Image.Png;

            images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
        }

        return images;
    }
}
