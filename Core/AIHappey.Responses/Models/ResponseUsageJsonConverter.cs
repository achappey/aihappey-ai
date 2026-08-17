using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Responses;

public sealed class ResponseUsageJsonConverter : JsonConverter<ResponseUsage>
{
    private static readonly HashSet<string> KnownRootProperties = new(StringComparer.Ordinal)
    {
        "input_tokens",
        "input_tokens_details",
        "output_tokens",
        "output_tokens_details",
        "total_tokens"
    };

    public override ResponseUsage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Responses usage must be a JSON object.");

        var raw = root.Clone();
        var inputTokens = ReadInt(root, "input_tokens", "prompt_tokens", "inputTokens", "promptTokens", "total_input_tokens", "totalInputTokens");
        var outputTokens = ReadInt(root, "output_tokens", "completion_tokens", "outputTokens", "completionTokens", "total_output_tokens", "totalOutputTokens");
        var totalTokens = ReadInt(root, "total_tokens", "totalTokens")
            ?? (inputTokens is not null && outputTokens is not null ? inputTokens + outputTokens : null);

        var cachedTokens = ReadNestedInt(root, "input_tokens_details", "cached_tokens")
            ?? ReadNestedInt(root, "prompt_tokens_details", "cached_tokens")
            ?? ReadInt(root, "cache_read_input_tokens", "cacheReadInputTokens", "cached_input_tokens", "cachedInputTokens", "total_cached_tokens", "totalCachedTokens");
        var cacheWriteTokens = ReadNestedInt(root, "input_tokens_details", "cache_write_tokens")
            ?? ReadInt(root, "cache_creation_input_tokens", "cacheCreationInputTokens", "cache_write_input_tokens", "cached_input_write_tokens", "cacheWriteInputTokens");
        var reasoningTokens = ReadNestedInt(root, "output_tokens_details", "reasoning_tokens")
            ?? ReadNestedInt(root, "output_tokens_details", "thinking_tokens")
            ?? ReadNestedInt(root, "completion_tokens_details", "reasoning_tokens")
            ?? ReadInt(root, "reasoning_tokens", "thinking_tokens", "total_thought_tokens", "totalThoughtTokens");

        return new ResponseUsage
        {
            InputTokens = inputTokens,
            InputTokensDetails = cachedTokens is not null || cacheWriteTokens is not null
                ? new ResponseInputTokensDetails
                {
                    CachedTokens = cachedTokens,
                    CacheWriteTokens = cacheWriteTokens
                }
                : null,
            OutputTokens = outputTokens,
            OutputTokensDetails = reasoningTokens is not null
                ? new ResponseOutputTokensDetails { ReasoningTokens = reasoningTokens }
                : null,
            TotalTokens = totalTokens,
            AdditionalProperties = root.EnumerateObject()
                .Where(property => !KnownRootProperties.Contains(property.Name))
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal),
            Raw = raw
        };
    }

    public override void Write(Utf8JsonWriter writer, ResponseUsage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteNumber(writer, "input_tokens", value.InputTokens);

        if (value.InputTokensDetails is not null)
        {
            writer.WritePropertyName("input_tokens_details");
            JsonSerializer.Serialize(writer, value.InputTokensDetails, options);
        }

        WriteNumber(writer, "output_tokens", value.OutputTokens);

        if (value.OutputTokensDetails is not null)
        {
            writer.WritePropertyName("output_tokens_details");
            JsonSerializer.Serialize(writer, value.OutputTokensDetails, options);
        }

        WriteNumber(writer, "total_tokens", value.TotalTokens);

        if (value.AdditionalProperties is not null)
        {
            foreach (var property in value.AdditionalProperties)
            {
                // Canonical typed properties above are authoritative. Do not let
                // extension data emit a duplicate property with different casing.
                if (KnownRootProperties.Contains(property.Key)
                    || KnownRootProperties.Any(known => string.Equals(
                        known,
                        property.Key,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                writer.WritePropertyName(property.Key);
                property.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is not null)
            writer.WriteNumber(name, value.Value);
    }

    private static int? ReadNestedInt(JsonElement root, string objectName, string propertyName)
        => root.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadInt(nested, propertyName)
            : null;

    private static int? ReadInt(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
                return number;
        }

        return null;
    }
}
