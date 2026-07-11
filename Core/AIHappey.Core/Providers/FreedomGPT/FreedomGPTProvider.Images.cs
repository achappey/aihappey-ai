using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.FreedomGPT;

public partial class FreedomGPTProvider
{
    private static readonly JsonSerializerOptions FreedomGptImageJsonOptions = new(JsonSerializerDefaults.Web)
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
        List<object> warnings = [];

        if (!string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "size" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (request.ProviderOptions?.Count > 0)
            warnings.Add(new { type = "unsupported", feature = "providerOptions" });

        int? numberOfImages = request.N;
        if (numberOfImages is <= 0)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "FreedomGPT numberOfImages must be greater than zero; omitted invalid value."
            });
            numberOfImages = null;
        }

        var payload = new
        {
            model = request.Model,
            prompt = request.Prompt,
            numberOfImages
        };

        var body = JsonSerializer.Serialize(payload, FreedomGptImageJsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var httpResponse = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"FreedomGPT image generation failed ({(int)httpResponse.StatusCode})."
                : $"FreedomGPT image generation failed ({(int)httpResponse.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("FreedomGPT image generation returned no data array.");

        List<string> images = [];
        foreach (var item in dataEl.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;

            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            using var imageResponse = await _client.GetAsync(url, cancellationToken);
            var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!imageResponse.IsSuccessStatusCode || imageBytes.Length == 0)
                throw new InvalidOperationException($"Failed to download FreedomGPT image from returned URL ({(int)imageResponse.StatusCode}).");

            var mediaType = imageResponse.Content.Headers.ContentType?.MediaType
                ?? GuessFreedomGptImageMediaType(url)
                ?? MediaTypeNames.Image.Png;

            images.Add(Convert.ToBase64String(imageBytes).ToDataUrl(mediaType));
        }

        if (images.Count == 0)
            throw new InvalidOperationException("FreedomGPT image generation returned no downloadable images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new ()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()) 
            }
        };
    }

    private static string? GuessFreedomGptImageMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.Contains(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";
        if (url.Contains(".jpg", StringComparison.OrdinalIgnoreCase) || url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Jpeg;
        if (url.Contains(".png", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Png;

        return null;
    }
}
