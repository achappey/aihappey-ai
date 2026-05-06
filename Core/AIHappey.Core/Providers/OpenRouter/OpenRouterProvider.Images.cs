using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.OpenRouter;

public partial class OpenRouterProvider
{
    private static readonly JsonSerializerOptions OpenRouterImageJsonOptions = new(JsonSerializerDefaults.Web)
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

        var rawOpenRouterOptions = ReadOpenRouterImageProviderOptions(request);

        if (request.Mask is not null && !OpenRouterImageHasRawOption(rawOpenRouterOptions, "mask"))
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (request.N is not null && !OpenRouterImageHasRawOption(rawOpenRouterOptions, "n"))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "OpenRouter image generation docs do not define a generic n parameter. Use providerOptions.openrouter for model-specific passthrough fields."
            });
        }

        if (request.Seed is not null && !OpenRouterImageHasRawOption(rawOpenRouterOptions, "seed"))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed",
                details = "OpenRouter image generation docs do not define a generic seed parameter. Use providerOptions.openrouter for model-specific passthrough fields."
            });
        }

        var payload = BuildOpenRouterImagePayload(request, rawOpenRouterOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OpenRouterImageJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OpenRouter image request failed ({(int)resp.StatusCode})."
                : $"OpenRouter image request failed ({(int)resp.StatusCode}): {raw}");
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        var images = ExtractOpenRouterImages(root);
        if (images.Count == 0)
            throw new InvalidOperationException("OpenRouter image response did not contain generated images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ExtractOpenRouterImageUsage(root),
            ProviderMetadata = BuildOpenRouterImageProviderMetadata(payload, root, resp, rawOpenRouterOptions),
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = ReadOpenRouterImageString(root, "model") ?? request.Model,
                Body = root
            }
        };
    }

    private static Dictionary<string, object?> BuildOpenRouterImagePayload(
        ImageRequest request,
        JsonElement? rawOpenRouterOptions)
    {
        var imageConfig = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            imageConfig["aspect_ratio"] = request.AspectRatio;

        if (!string.IsNullOrWhiteSpace(request.Size))
            imageConfig["image_size"] = request.Size;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = BuildOpenRouterImageMessages(request),
            ["modalities"] = new[] { "image", "text" },
            ["stream"] = false
        };

        if (imageConfig.Count > 0)
            payload["image_config"] = imageConfig;

        MergeOpenRouterImageProviderOptions(payload, rawOpenRouterOptions);

        return payload;
    }

    private static object[] BuildOpenRouterImageMessages(ImageRequest request)
    {
        var files = request.Files?.ToList() ?? [];

        if (files.Count == 0)
        {
            return
            [
                new
                {
                    role = "user",
                    content = request.Prompt
                }
            ];
        }

        var content = new List<object>
        {
            new
            {
                type = "text",
                text = request.Prompt
            }
        };

        foreach (var file in files)
        {
            content.Add(new
            {
                type = "image_url",
                image_url = new
                {
                    url = NormalizeOpenRouterImageInput(file)
                }
            });
        }

        return
        [
            new
            {
                role = "user",
                content
            }
        ];
    }

    private static void MergeOpenRouterImageProviderOptions(
        Dictionary<string, object?> payload,
        JsonElement? rawOpenRouterOptions)
    {
        if (rawOpenRouterOptions is not { ValueKind: JsonValueKind.Object } providerOptions)
            return;

        foreach (var property in providerOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private static JsonElement? ReadOpenRouterImageProviderOptions(ImageRequest request)
    {
        if (request.ProviderOptions is null)
            return null;

        return request.ProviderOptions.TryGetValue("openrouter", out var providerOptions)
               && providerOptions.ValueKind == JsonValueKind.Object
            ? providerOptions.Clone()
            : null;
    }

    private static bool OpenRouterImageHasRawOption(JsonElement? rawOpenRouterOptions, string propertyName)
        => rawOpenRouterOptions is { ValueKind: JsonValueKind.Object } providerOptions
           && providerOptions.TryGetProperty(propertyName, out var property)
           && property.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private static List<string> ExtractOpenRouterImages(JsonElement root)
    {
        List<string> images = [];

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                CollectOpenRouterImageParts(message, images);

            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                CollectOpenRouterImageParts(delta, images);
        }

        return [.. images.Distinct(StringComparer.Ordinal)];
    }

    private static void CollectOpenRouterImageParts(JsonElement messageOrDelta, List<string> images)
    {
        if (!messageOrDelta.TryGetProperty("images", out var imageParts) || imageParts.ValueKind != JsonValueKind.Array)
            return;

        foreach (var imagePart in imageParts.EnumerateArray())
        {
            if (imagePart.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String
                && !string.Equals(typeEl.GetString(), "image_url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!imagePart.TryGetProperty("image_url", out var imageUrl) || imageUrl.ValueKind != JsonValueKind.Object)
                continue;

            if (!imageUrl.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;

            var url = urlEl.GetString();
            if (!string.IsNullOrWhiteSpace(url))
                images.Add(NormalizeOpenRouterImageOutput(url));
        }
    }

    private static ImageUsageData? ExtractOpenRouterImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var usageData = new ImageUsageData
        {
            InputTokens = ReadOpenRouterImageInt(usage, "prompt_tokens") ?? ReadOpenRouterImageInt(usage, "input_tokens"),
            OutputTokens = ReadOpenRouterImageInt(usage, "completion_tokens") ?? ReadOpenRouterImageInt(usage, "output_tokens"),
            TotalTokens = ReadOpenRouterImageInt(usage, "total_tokens")
        };

        if (!usageData.InputTokens.HasValue && !usageData.OutputTokens.HasValue && !usageData.TotalTokens.HasValue)
            return null;

        return usageData;
    }

    private Dictionary<string, JsonElement> BuildOpenRouterImageProviderMetadata(
        Dictionary<string, object?> payload,
        JsonElement root,
        HttpResponseMessage response,
        JsonElement? rawOpenRouterOptions)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["request"] = payload,
            ["response"] = root,
            ["statusCode"] = (int)response.StatusCode
        };

        if (rawOpenRouterOptions is { ValueKind: JsonValueKind.Object } rawOptions)
            metadata["providerOptions"] = rawOptions;

        return new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata, OpenRouterImageJsonOptions)
        };
    }

    private static string NormalizeOpenRouterImageInput(ImageFile file)
    {
        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Image.Png
            : file.MediaType;

        return file.Data.ToDataUrl(mediaType);
    }

    private static string NormalizeOpenRouterImageOutput(string value)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return value.ToDataUrl(MediaTypeNames.Image.Png);
    }

    private static string? ReadOpenRouterImageString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? ReadOpenRouterImageInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }
}
