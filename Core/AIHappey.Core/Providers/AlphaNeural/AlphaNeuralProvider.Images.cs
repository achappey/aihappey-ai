using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.AlphaNeural;

public partial class AlphaNeuralProvider
{
    private async Task<ImageResponse> ImageRequestAlphaNeural(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var warnings = BuildAlphaNeuralImageWarnings(imageRequest);
        var payload = BuildAlphaNeuralImagePayload(imageRequest);
        var json = JsonSerializer.Serialize(payload, JsonOpts);

        using var resp = await _client.PostAsync("v1/images/generations",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json), cancellationToken);

        var text = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"AlphaNeural API error: {resp.StatusCode}: {text}");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.Clone();
        var images = new List<string>();

        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataEl.EnumerateArray())
            {
                var image = await NormalizeAlphaNeuralImageItemAsync(item, cancellationToken);

                if (!string.IsNullOrWhiteSpace(image))
                    images.Add(image);
            }
        }

        return new ImageResponse()
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public static Dictionary<string, object?> BuildAlphaNeuralImagePayload(ImageRequest imageRequest)
    {
        var metadata = GetAlphaNeuralImageMetadata(imageRequest);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = imageRequest.Model,
            ["prompt"] = imageRequest.Prompt,
            ["n"] = imageRequest.N,
            ["size"] = imageRequest.Size,
            ["quality"] = metadata.TryGetString("quality"),
            ["style"] = metadata.TryGetString("style"),
            ["user"] = metadata.TryGetString("user")
        };

        var responseFormat = metadata.TryGetString("response_format")
            ?? metadata.TryGetString("responseFormat");

        if (!string.IsNullOrWhiteSpace(responseFormat) && !IsGptImageModel(imageRequest.Model))
            payload["response_format"] = responseFormat;

        return payload;
    }

    private static IEnumerable<object> BuildAlphaNeuralImageWarnings(ImageRequest imageRequest)
    {
        var warnings = new List<object>();

        if (imageRequest.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "AlphaNeural image generation route supports text-to-image requests. Input images were ignored."
            });
        }

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        if (imageRequest.Seed.HasValue)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed"
            });
        }

        if (!string.IsNullOrWhiteSpace(imageRequest.AspectRatio) && string.IsNullOrWhiteSpace(imageRequest.Size))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aspectRatio",
                details = "AlphaNeural follows the OpenAI Images API size field. Provide size when a specific output dimension is required."
            });
        }

        var metadata = GetAlphaNeuralImageMetadata(imageRequest);
        var responseFormat = metadata.TryGetString("response_format")
            ?? metadata.TryGetString("responseFormat");

        if (!string.IsNullOrWhiteSpace(responseFormat) && IsGptImageModel(imageRequest.Model))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "response_format",
                details = "GPT image models always return base64 and do not support response_format."
            });
        }

        return warnings;
    }

    private async Task<string?> NormalizeAlphaNeuralImageItemAsync(JsonElement item, CancellationToken cancellationToken)
    {
        var b64 = item.TryGetString("b64_json")
            ?? item.TryGetString("base64")
            ?? item.TryGetString("data");

        if (!string.IsNullOrWhiteSpace(b64))
            return b64.ToDataUrl(GuessAlphaNeuralImageMediaType(item) ?? MediaTypeNames.Image.Png);

        var url = item.TryGetString("url");
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var bytes = await _client.GetByteArrayAsync(url, cancellationToken);
        var mediaType = GuessAlphaNeuralImageMediaType(item) ?? GuessAlphaNeuralImageMediaTypeFromUrl(url) ?? MediaTypeNames.Image.Png;

        return Convert.ToBase64String(bytes).ToDataUrl(mediaType);
    }

    private static JsonElement GetAlphaNeuralImageMetadata(ImageRequest imageRequest)
        => imageRequest.GetProviderMetadata<JsonElement>(nameof(AlphaNeural).ToLowerInvariant());

    internal static bool IsAlphaNeuralImageModel(string? model)
    {
        var modelId = NormalizeAlphaNeuralModelId(model);

        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        return modelId.Contains("image")
            || modelId.Contains("dall-e")
            || modelId.Contains("dalle");
    }

    private static bool IsGptImageModel(string? model)
    {
        var modelId = NormalizeAlphaNeuralModelId(model);

        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        return modelId.StartsWith("gpt-image-", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("/gpt-image-", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAlphaNeuralModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var split = model.SplitModelId();
        var modelId = string.IsNullOrWhiteSpace(split.Model) ? model : split.Model;

        return modelId.ToLowerInvariant();
    }

    private static string? GuessAlphaNeuralImageMediaType(JsonElement item)
        => item.TryGetString("mime_type")
            ?? item.TryGetString("mimeType")
            ?? item.TryGetString("content_type")
            ?? item.TryGetString("format") switch
            {
                "png" => MediaTypeNames.Image.Png,
                "jpg" or "jpeg" => MediaTypeNames.Image.Jpeg,
                "webp" => "image/webp",
                var value when !string.IsNullOrWhiteSpace(value) && value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => value,
                _ => null
            };

    private static string? GuessAlphaNeuralImageMediaTypeFromUrl(string url)
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
            _ => null
        };
    }
}
