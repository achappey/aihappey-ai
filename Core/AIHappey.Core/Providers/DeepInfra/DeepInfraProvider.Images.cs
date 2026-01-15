using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    private static readonly JsonSerializerOptions ImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (imageRequest.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var size = imageRequest.Size;
        if (string.IsNullOrWhiteSpace(size) && !string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
        {
            var inferred = imageRequest.AspectRatio.InferSizeFromAspectRatio();
            if (inferred is not null)
                size = $"{inferred.Value.width}x{inferred.Value.height}";
        }

        var payload = JsonSerializer.Serialize(new
        {
            model = imageRequest.Model,
            prompt = imageRequest.Prompt,
            num_images = imageRequest.N ?? 1,
            size,
        }, ImageJson);

        using var resp = await _client.PostAsync(
            "v1/openai/images/generations",
            new StringContent(payload, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var images = await ExtractImagesAsDataUrlsAsync(raw, cancellationToken);
        if (images.Count == 0)
            throw new Exception("DeepInfra returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = JsonDocument.Parse(raw).RootElement.Clone()
            }
        };
    }

    private async Task<List<string>> ExtractImagesAsDataUrlsAsync(string rawJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        List<string> images = [];

        foreach (var item in data.EnumerateArray())
        {
            // OpenAI-like responses may contain b64_json or url.
            if (item.TryGetProperty("b64_json", out var b64Prop))
            {
                var b64 = b64Prop.GetString();
                if (!string.IsNullOrWhiteSpace(b64))
                    images.Add(b64.ToDataUrl(MediaTypeNames.Image.Png));
                continue;
            }

            if (item.TryGetProperty("url", out var urlProp))
            {
                var url = urlProp.GetString();
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var bytes = await _client.GetByteArrayAsync(url, ct);
                var mime = GuessImageMimeTypeFromUrl(url);
                images.Add(Convert.ToBase64String(bytes).ToDataUrl(mime));
            }
        }

        return images;
    }

    private static string GuessImageMimeTypeFromUrl(string url)
    {
        if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Jpeg;

        if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";

        if (url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Gif;

        return MediaTypeNames.Image.Png;
    }
}

