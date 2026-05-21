using System.Text.Json;

namespace AIHappey.Interactions.Mapping;

public static partial class InteractionsUnifiedMapper
{
    private static object? NormalizeInteractionResponseFormat(
        object? responseFormat,
        string? responseMimeType,
        InteractionGenerationConfig? generationConfig)
    {
        var normalized = NormalizeTextResponseFormat(responseFormat, responseMimeType);
        var imageFormat = CreateImageResponseFormat(generationConfig?.ImageConfig);
        if (generationConfig is not null)
            generationConfig.ImageConfig = null;

        return (normalized, imageFormat) switch
        {
            (null, null) => null,
            (null, not null) => imageFormat,
            (not null, null) => normalized,
            (JsonElement json, not null) when json.ValueKind == JsonValueKind.Array => AppendResponseFormat(json, imageFormat),
            (not null, not null) => new object[] { normalized, imageFormat }
        };
    }

    private static object? NormalizeTextResponseFormat(object? responseFormat, string? responseMimeType)
    {
        if (responseFormat is null && string.IsNullOrWhiteSpace(responseMimeType))
            return null;

        if (responseFormat is JsonElement json)
            return NormalizeTextResponseFormat(json, responseMimeType);

        var map = ToJsonMap(responseFormat);
        if (map.TryGetValue("type", out var typeValue)
            && string.Equals(ToJsonString(typeValue, string.Empty), "text", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(responseMimeType) && !map.ContainsKey("mime_type"))
                map["mime_type"] = responseMimeType;
            return map;
        }

        var wrapper = new Dictionary<string, object?>
        {
            ["type"] = "text"
        };

        if (!string.IsNullOrWhiteSpace(responseMimeType))
            wrapper["mime_type"] = responseMimeType;

        if (responseFormat is not null)
            wrapper["schema"] = CloneIfJsonElement(responseFormat);

        return wrapper;
    }

    private static object? NormalizeTextResponseFormat(JsonElement responseFormat, string? responseMimeType)
    {
        if (responseFormat.ValueKind == JsonValueKind.Array)
        {
            var entries = responseFormat.EnumerateArray()
                .Select(entry => entry.ValueKind == JsonValueKind.Object ? NormalizeTextResponseFormatObject(entry, responseMimeType) : entry.Clone())
                .ToList<object?>();
            return entries;
        }

        if (responseFormat.ValueKind == JsonValueKind.Object)
            return NormalizeTextResponseFormatObject(responseFormat, responseMimeType);

        return responseFormat.Clone();
    }

    private static object NormalizeTextResponseFormatObject(JsonElement responseFormat, string? responseMimeType)
    {
        var map = responseFormat.EnumerateObject().ToDictionary(a => a.Name, a => (object?)a.Value.Clone());
        if (map.TryGetValue("type", out var typeValue)
            && string.Equals(ToJsonString(typeValue, string.Empty), "text", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(responseMimeType) && !map.ContainsKey("mime_type"))
                map["mime_type"] = responseMimeType;
            return map;
        }

        var wrapper = new Dictionary<string, object?> { ["type"] = "text" };
        if (!string.IsNullOrWhiteSpace(responseMimeType))
            wrapper["mime_type"] = responseMimeType;
        wrapper["schema"] = responseFormat.Clone();
        return wrapper;
    }

    private static object? CreateImageResponseFormat(InteractionImageConfig? imageConfig)
    {
        if (imageConfig is null)
            return null;

        var result = new Dictionary<string, object?>
        {
            ["type"] = "image",
            ["mime_type"] = "image/jpeg",
            ["aspect_ratio"] = imageConfig.AspectRatio,
            ["image_size"] = imageConfig.ImageSize
        };

        if (imageConfig.AdditionalProperties is not null)
        {
            foreach (var property in imageConfig.AdditionalProperties)
                result[property.Key] = property.Value.Clone();
        }

        return result.Where(a => a.Value is not null).ToDictionary(a => a.Key, a => a.Value);
    }

    private static object AppendResponseFormat(JsonElement array, object imageFormat)
        => array.EnumerateArray().Select(a => (object?)a.Clone()).Append(imageFormat).ToArray();
}
