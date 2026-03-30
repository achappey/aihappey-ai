using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runtimo;

public partial class RuntimoProvider
{
    private async Task<ImageResponse> RuntimoImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var files = request.Files?.ToList() ?? [];

        if (files.Count > 1)
            warnings.Add(new { type = "unsupported", feature = "files.additional" });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (!string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "size", details = "Pass model-specific size through providerOptions.runtimo.input." });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = "Pass model-specific aspect ratio through providerOptions.runtimo.input." });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed", details = "Pass model-specific seed through providerOptions.runtimo.input." });

        if (request.N is not null)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Pass model-specific image count through providerOptions.runtimo.input." });

        var modelPath = NormalizeModelPath(request.Model, GetIdentifier());
        var endpoint = $"v1/models/{modelPath}/call";
        var metadata = GetImageProviderMetadata(request, GetIdentifier());
        var payload = BuildRuntimoPayload(request.Prompt, NormalizeImageInput(files.FirstOrDefault()), metadata);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, RuntimoMediaJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runtimo image request failed ({(int)response.StatusCode}) [{endpoint}]: {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();

        var images = await ExtractRuntimoImagesAsync(root, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("Runtimo image response did not contain any images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = BuildRuntimoProviderMetadata(endpoint, root),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    endpoint,
                    body = root
                }
            }
        };
    }

    private async Task<List<string>> ExtractRuntimoImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        List<string> images = [];

        if (TryGetPropertyIgnoreCase(root, "images", out var imagesElement) && imagesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in imagesElement.EnumerateArray())
            {
                var image = await NormalizeImageOutputAsync(item, cancellationToken);
                if (!string.IsNullOrWhiteSpace(image))
                    images.Add(image);
            }
        }

        if (images.Count == 0 && TryGetPropertyIgnoreCase(root, "image", out var imageElement))
        {
            var image = await NormalizeImageOutputAsync(imageElement, cancellationToken);
            if (!string.IsNullOrWhiteSpace(image))
                images.Add(image);
        }

        return images;
    }

    private async Task<string?> NormalizeImageOutputAsync(JsonElement element, CancellationToken cancellationToken)
    {
        if (element.ValueKind == JsonValueKind.String)
            return await NormalizeImageOutputAsync(element.GetString(), null, cancellationToken);

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var mediaType = GetString(element, "content_type", "media_type", "mime_type", "mimeType");

        var url = GetString(element, "url", "image_url", "imageUrl", "download_url", "downloadUrl", "file_url", "fileUrl");
        if (!string.IsNullOrWhiteSpace(url))
            return await NormalizeImageOutputAsync(url, mediaType, cancellationToken);

        var base64 = GetString(element, "b64_json", "base64", "data");
        if (!string.IsNullOrWhiteSpace(base64))
            return await NormalizeImageOutputAsync(base64, mediaType, cancellationToken);

        if (TryGetPropertyIgnoreCase(element, "image", out var nestedImage))
            return await NormalizeImageOutputAsync(nestedImage, cancellationToken);

        return null;
    }

    private async Task<string?> NormalizeImageOutputAsync(string? value, string? mediaType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (LooksLikeDataUrl(value))
            return value;

        if (LooksLikeUrl(value))
            return await DownloadImageAsDataUrlAsync(value, mediaType, cancellationToken);

        return value.ToDataUrl(mediaType ?? MediaTypeNames.Image.Png);
    }
}
