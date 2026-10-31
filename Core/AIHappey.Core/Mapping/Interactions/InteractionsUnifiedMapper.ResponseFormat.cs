using System.Text.Json;

namespace AIHappey.Interactions.Mapping;

public static partial class InteractionsUnifiedMapper
{
    private static object? NormalizeInteractionResponseFormat(
        object? providerResponseFormat,
        object? unifiedResponseFormat,
        string? responseMimeType,
        InteractionGenerationConfig? generationConfig)
    {
        var normalized = MergeInteractionResponseFormats(
            providerResponseFormat,
            CreateUnifiedTextResponseFormat(unifiedResponseFormat, responseMimeType));
        var imageFormat = CreateImageResponseFormat(generationConfig?.ImageConfig);
        if (generationConfig is not null)
            generationConfig.ImageConfig = null;

        if (normalized is null)
            return imageFormat;
        if (imageFormat is null)
            return normalized;

        return AppendResponseFormat(normalized, imageFormat);
    }

    private static object? CreateUnifiedTextResponseFormat(object? responseFormat, string? responseMimeType)
    {
        if (responseFormat is null)
            return null;

        var element = JsonSerializer.SerializeToElement(responseFormat, Json);
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("type", out var type)
            || !string.Equals(type.GetString(), "json_schema", StringComparison.OrdinalIgnoreCase)
            || !element.TryGetProperty("json_schema", out var jsonSchema)
            || jsonSchema.ValueKind != JsonValueKind.Object
            || !jsonSchema.TryGetProperty("schema", out var schema))
            return null;

        return new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["mime_type"] = "application/json",
            ["schema"] = schema.Clone()
        };
    }

    private static object? MergeInteractionResponseFormats(object? providerResponseFormat, object? unifiedTextFormat)
    {
        if (providerResponseFormat is null)
            return unifiedTextFormat;

        // Provider-native response_format is a raw passthrough unless a unified
        // structured-output schema needs to supply a missing text schema.
        if (unifiedTextFormat is null || HasProviderTextSchema(providerResponseFormat))
            return CloneIfJsonElement(providerResponseFormat);

        var providerElement = JsonSerializer.SerializeToElement(providerResponseFormat, Json);
        if (providerElement.ValueKind == JsonValueKind.Array)
        {
            var entries = providerElement.EnumerateArray().Select(a => (object?)a.Clone()).ToList();
            var textIndex = entries.FindIndex(IsSchemaLessTextFormat);
            if (textIndex >= 0)
                entries[textIndex] = unifiedTextFormat;
            else
                entries.Add(unifiedTextFormat);
            return entries;
        }

        if (IsSchemaLessTextFormat(providerResponseFormat))
            return unifiedTextFormat;

        return new object?[] { CloneIfJsonElement(providerResponseFormat), unifiedTextFormat };
    }

    private static bool HasProviderTextSchema(object responseFormat)
    {
        var element = JsonSerializer.SerializeToElement(responseFormat, Json);
        return element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Any(HasTextSchema)
            : HasTextSchema(element);
    }

    private static bool HasTextSchema(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty("type", out var type)
           && string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase)
           && element.TryGetProperty("schema", out var schema)
           && schema.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private static bool IsSchemaLessTextFormat(object? responseFormat)
    {
        if (responseFormat is null)
            return false;

        var element = responseFormat is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(responseFormat, Json);
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty("type", out var type)
               && string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase)
               && (!element.TryGetProperty("schema", out var schema)
                   || schema.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
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

    private static object AppendResponseFormat(object responseFormat, object imageFormat)
    {
        var element = JsonSerializer.SerializeToElement(responseFormat, Json);
        return element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(a => (object?)a.Clone()).Append(imageFormat).ToArray()
            : new object[] { responseFormat, imageFormat };
    }
}
