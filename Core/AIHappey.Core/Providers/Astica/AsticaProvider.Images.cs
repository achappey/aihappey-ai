using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Astica;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Astica;

public partial class AsticaProvider
{
    private static readonly JsonSerializerOptions ImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Uri AsticaDesignGenerateImageUri = new("https://design.astica.ai/generate_image");

    private async Task<ImageResponse> ImageRequestInternal(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        var apiKey = ResolveRequiredApiKey();
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (imageRequest.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Astica generate_image returns one output URL per request." });

        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (!string.IsNullOrWhiteSpace(imageRequest.Size))
            warnings.Add(new { type = "ignored", feature = "size", reason = "Astica generate_image currently returns 1024x1024 output." });

        if (!string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
            warnings.Add(new { type = "ignored", feature = "aspectRatio", reason = "Astica generate_image currently returns 1024x1024 output." });

        var metadata = imageRequest.GetProviderMetadata<AsticaImageProviderMetadata>(GetIdentifier());
        var modelVersion = ParseImageModelVersion(imageRequest.Model);

        var payload = new Dictionary<string, object?>
        {
            ["tkn"] = apiKey,
            ["modelVersion"] = modelVersion,
            ["prompt"] = imageRequest.Prompt,
            ["prompt_negative"] = string.IsNullOrWhiteSpace(metadata?.PromptNegative) ? null : metadata!.PromptNegative!.Trim(),
            ["generate_quality"] = NormalizeGenerateQuality(metadata?.GenerateQuality),
            ["generate_lossless"] = metadata?.GenerateLossless,
            ["seed"] = imageRequest.Seed,
            ["moderate"] = metadata?.Moderate,
            ["low_priority"] = metadata?.LowPriority
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, AsticaDesignGenerateImageUri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, ImageJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} image request failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var status = ReadString(doc.RootElement, "status");
        if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{ProviderName} image request failed: {raw}");

        var outputUrl = ReadString(doc.RootElement, "output");
        if (string.IsNullOrWhiteSpace(outputUrl))
            throw new InvalidOperationException($"{ProviderName} image response did not include output URL: {raw}");

        var imageDataUrl = await FetchImageAsDataUrl(outputUrl!, cancellationToken);

        return new ImageResponse
        {
            Images = [imageDataUrl],
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    modelVersion,
                    output = outputUrl,
                    generateQuality = ReadString(doc.RootElement, "generate_quality"),
                    generateLossless = ReadString(doc.RootElement, "generate_lossless"),
                    seed = ReadString(doc.RootElement, "seed"),
                    response = JsonSerializer.Deserialize<JsonElement>(raw)
                })
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = JsonSerializer.SerializeToElement(new
                {
                    response = JsonSerializer.Deserialize<JsonElement>(raw)
                })
            }
        };
    }

    private static string ParseImageModelVersion(string model)
    {
        var trimmed = model.Trim();

        if (trimmed.StartsWith($"{ProviderId}/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[(ProviderId.Length + 1)..];

        if (string.IsNullOrWhiteSpace(trimmed))
            throw new ArgumentException($"Model must be '{ProviderId}/[modelVersion]'.", nameof(model));

        return trimmed;
    }

    private static string? NormalizeGenerateQuality(string? quality)
    {
        if (string.IsNullOrWhiteSpace(quality))
            return null;

        var value = quality.Trim().ToLowerInvariant();
        return value is "high" or "standard" or "fast" or "faster"
            ? value
            : throw new ArgumentException("Astica generate_quality must be one of: high, standard, fast, faster.", nameof(quality));
    }

    private async Task<string> FetchImageAsDataUrl(string url, CancellationToken cancellationToken)
    {
        using var imageResp = await _client.GetAsync(url, cancellationToken);
        if (!imageResp.IsSuccessStatusCode)
        {
            var errorBody = await imageResp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"{ProviderName} image download failed ({(int)imageResp.StatusCode}): {errorBody}");
        }

        var bytes = await imageResp.Content.ReadAsByteArrayAsync(cancellationToken);
        var mediaType = imageResp.Content.Headers.ContentType?.MediaType
            ?? GuessImageMimeType(url);

        return Convert.ToBase64String(bytes).ToDataUrl(mediaType);
    }

    private static string GuessImageMimeType(string url)
    {
        if (url.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Png;

        if (url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";

        if (url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Gif;

        if (url.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            return "image/bmp";

        if (url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return "image/svg+xml";

        return MediaTypeNames.Image.Jpeg;
    }
}

