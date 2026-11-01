using System.Text.Json;

namespace AIHappey.Interactions.Mapping;

public static partial class InteractionsUnifiedMapper
{
    private static object? NormalizeInteractionResponseFormat(
        object? providerResponseFormat,
        object? unifiedResponseFormat)
        => MergeInteractionResponseFormats(
            providerResponseFormat,
            CreateUnifiedTextResponseFormat(unifiedResponseFormat));

    private static object? CreateUnifiedTextResponseFormat(object? responseFormat)
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
}
