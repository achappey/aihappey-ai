using AIHappey.Common.Extensions;
using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Net.Mime;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Lyceum;

public partial class LyceumProvider
{
    private static readonly JsonSerializerOptions LyceumImageJsonOptions = new(JsonSerializerDefaults.Web)
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

        if (!string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "size" });
        if (request.N is not null)
            warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["aspect_ratio"] = string.IsNullOrWhiteSpace(request.AspectRatio) ? "1:1" : request.AspectRatio
        };

        MergeLyceumProviderOptions(payload, request.GetProviderMetadata<JsonElement>(GetIdentifier()));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, LyceumImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lyceum image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();

        var imageUrl = LyceumReadStringProperty(root, "image_url");
        if (string.IsNullOrWhiteSpace(imageUrl))
            throw new InvalidOperationException("Lyceum image generation response missing 'image_url'.");

        var image = imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? imageUrl
            : await DownloadLyceumImageAsDataUrlAsync(imageUrl, cancellationToken);

        return new ImageResponse
        {
            Images = [image],
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<string> DownloadLyceumImageAsDataUrlAsync(string imageUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri))
            throw new InvalidOperationException($"Lyceum image generation returned an invalid image_url: {imageUrl}");

        using var imageResponse = await _client.GetAsync(imageUri, cancellationToken);
        var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!imageResponse.IsSuccessStatusCode)
        {
            var error = TryDecodeLyceumUtf8(imageBytes);
            throw new InvalidOperationException($"Lyceum image download failed ({(int)imageResponse.StatusCode}): {error ?? imageResponse.ReasonPhrase}");
        }

        if (imageBytes.Length == 0)
            throw new InvalidOperationException("Lyceum image download returned an empty response.");

        var mediaType = imageResponse.Content.Headers.ContentType?.MediaType
            ?? GuessLyceumImageMediaType(imageUrl)
            ?? MediaTypeNames.Image.Png;

        return Convert.ToBase64String(imageBytes).ToDataUrl(mediaType);
    }

    private static void MergeLyceumProviderOptions(Dictionary<string, object?> payload, JsonElement providerOptions)
    {
        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private static string? LyceumReadStringProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? GuessLyceumImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var value = url.Trim().ToLowerInvariant();
        if (value.Contains(".png")) return MediaTypeNames.Image.Png;
        if (value.Contains(".jpg") || value.Contains(".jpeg")) return MediaTypeNames.Image.Jpeg;
        if (value.Contains(".webp")) return "image/webp";
        if (value.Contains(".gif")) return MediaTypeNames.Image.Gif;
        if (value.Contains(".bmp")) return "image/bmp";
        if (value.Contains(".avif")) return "image/avif";

        return null;
    }

    private static string? TryDecodeLyceumUtf8(byte[] bytes)
    {
        try
        {
            return bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

}
