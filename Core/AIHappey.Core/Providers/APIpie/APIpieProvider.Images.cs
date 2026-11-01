using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.APIpie;

public partial class APIpieProvider
{
    private static readonly JsonSerializerOptions imageSettings = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = imageRequest.GetProviderMetadata<JsonElement>(GetIdentifier());

        var requestedN = imageRequest.N ?? 1;
        if (requestedN < 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = $"Requested n={requestedN} is invalid. Using n=1."
            });
            requestedN = 1;
        }

        if (requestedN > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = $"APIpie currently supports n=1. Requested n={requestedN}, using n=1."
            });
            requestedN = 1;
        }

        if (imageRequest.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "APIpie image generation expects image URL input via providerOptions.apipie.image when editing."
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

        var payload = GetAdditionalMetadata(metadata,
            "quality", "style", "image", "seed", "steps", "loras", "strength", "aspect_ratio", "text_layout",
            "prompt", "model", "n", "size", "response_format");
        payload["prompt"] = imageRequest.Prompt;
        payload["model"] = imageRequest.Model;
        payload["n"] = requestedN;
        payload["size"] = imageRequest.Size;
        payload["quality"] = GetMetadataValue<string>(metadata, "quality");
        payload["response_format"] = "b64_json";
        payload["style"] = GetMetadataValue<string>(metadata, "style");
        payload["image"] = GetMetadataValue<string>(metadata, "image");
        payload["seed"] = imageRequest.Seed ?? GetMetadataValue<int?>(metadata, "seed");
        payload["steps"] = GetMetadataValue<int?>(metadata, "steps");
        payload["loras"] = GetMetadataValue<IEnumerable<string>>(metadata, "loras");
        payload["strength"] = GetMetadataValue<double?>(metadata, "strength");
        payload["aspect_ratio"] = imageRequest.AspectRatio ?? GetMetadataValue<string>(metadata, "aspect_ratio");

        var textLayout = GetTextLayoutMetadata(metadata);
        if (textLayout is not null)
        {
            payload["text_layout"] = textLayout.Value;
        }

        // Null dictionary entries are not omitted by WhenWritingNull. Raw metadata values
        // are boxed JsonElements, so explicit provider nulls survive this CLR-null filter.
        var jsonBody = JsonSerializer.Serialize(
            payload.Where(property => property.Value is not null).ToDictionary(), imageSettings);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);

        if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            throw new Exception("No image data array returned from APIpie API.");

        List<string> images = [];

        foreach (var item in dataEl.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64El) || b64El.ValueKind != JsonValueKind.String)
                continue;

            var b64 = b64El.GetString();
            if (string.IsNullOrWhiteSpace(b64))
                continue;

            images.Add(b64.ToDataUrl("image/png"));
        }

        if (images.Count == 0)
            throw new Exception("No valid image data returned from APIpie API.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model
            }
        };
    }

    private static Dictionary<string, object?> GetAdditionalMetadata(JsonElement metadata, params string[] mappedProperties)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return [];

        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("Provider metadata must be a JSON object.");

        var payload = new Dictionary<string, object?>();
        foreach (var property in metadata.EnumerateObject())
        {
            if (mappedProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static T? GetMetadataValue<T>(JsonElement metadata, string name)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return default;

        T? result = default;
        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                result = property.Value.Deserialize<T>(JsonSerializerOptions.Web);
        }

        return result;
    }

    private static JsonElement? GetTextLayoutMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        JsonElement? result = null;
        foreach (var property in metadata.EnumerateObject())
        {
            if (!string.Equals(property.Name, "text_layout", StringComparison.OrdinalIgnoreCase))
                continue;

            ValidateTextLayout(property.Value);
            result = property.Value.ValueKind == JsonValueKind.Null
                ? null
                : property.Value.Clone();
        }

        return result;
    }

    private static void ValidateTextLayout(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return;

        if (value.ValueKind != JsonValueKind.Array)
            throw new JsonException("text_layout must be an array or null.");

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Null)
                continue;

            if (item.ValueKind != JsonValueKind.Object)
                throw new JsonException("text_layout items must be objects or null.");

            foreach (var property in item.EnumerateObject())
            {
                if (string.Equals(property.Name, "text", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    throw new JsonException("text_layout item text must be a string or null.");
                }

                if (string.Equals(property.Name, "bbox", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
                {
                    throw new JsonException("text_layout item bbox must be an array or null.");
                }
            }
        }
    }
}
