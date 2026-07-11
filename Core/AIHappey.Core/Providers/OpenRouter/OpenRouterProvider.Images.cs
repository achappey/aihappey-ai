using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
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

        var payload = BuildOpenRouterImagePayload(request, rawOpenRouterOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images")
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

        var providerKey = GetIdentifier();

        var providerMetadata = new Dictionary<string, JsonElement>();

        if (root.TryGetProperty("usage", out var usageEl)
            && usageEl.ValueKind == JsonValueKind.Object)
        {
            providerMetadata[providerKey] = JsonSerializer.SerializeToElement(new
            {
                usage = usageEl.Clone()
            }, JsonSerializerOptions.Web);
        }

        decimal? cost = null;

        if (root.TryGetProperty("usage", out var gatewayUsageEl)
            && gatewayUsageEl.ValueKind == JsonValueKind.Object
            && gatewayUsageEl.TryGetProperty("cost", out var costEl)
            && costEl.ValueKind == JsonValueKind.Number
            && costEl.TryGetDecimal(out var parsedCost))
        {
            cost = parsedCost;
        }

        if (cost is not null)
        {
            providerMetadata["gateway"] = JsonSerializer.SerializeToElement(new
            {
                cost
            }, JsonSerializerOptions.Web);
        }

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ExtractOpenRouterImageUsage(root),
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = ReadOpenRouterImageString(root, "model")?.ToModelId(GetIdentifier())
                    ?? request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static Dictionary<string, object?> BuildOpenRouterImagePayload(
        ImageRequest request,
        JsonElement? rawOpenRouterOptions)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        if (!string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = request.Size;

        if (request.N is not null)
            payload["n"] = request.N.Value;

        if (request.Seed is not null)
            payload["seed"] = request.Seed.Value;

        var files = request.Files?.ToList() ?? [];
        if (files.Count > 0)
            payload["input_references"] = files.Select(ToOpenRouterImageReference).ToList();

        MergeOpenRouterImageProviderOptions(payload, rawOpenRouterOptions);

        return payload;
    }

    private static Dictionary<string, object?> ToOpenRouterImageReference(ImageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return new Dictionary<string, object?>
        {
            ["type"] = "image_url",
            ["image_url"] = new Dictionary<string, object?>
            {
                ["url"] = NormalizeOpenRouterImageInput(file)
            }
        };
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

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var image in data.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object)
                continue;

            if (!image.TryGetProperty("b64_json", out var b64Json) || b64Json.ValueKind != JsonValueKind.String)
                continue;

            var value = b64Json.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var mediaType = ReadOpenRouterImageString(image, "media_type");
            images.Add(NormalizeOpenRouterImageOutput(value, mediaType));
        }

        return [.. images.Distinct(StringComparer.Ordinal)];
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

    private static string NormalizeOpenRouterImageOutput(string value, string? mediaType)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return value.ToDataUrl(string.IsNullOrWhiteSpace(mediaType) ? MediaTypeNames.Image.Png : mediaType);
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
