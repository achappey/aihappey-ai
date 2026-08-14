using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Responses;

/// <summary>
/// Keeps the public object-typed property source-compatible while ensuring JSON
/// entering or leaving a Responses object uses the typed Responses usage model.
/// </summary>
public sealed class ResponseUsageObjectJsonConverter : JsonConverter<object>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.Deserialize<ResponseUsage>(options)
            : null;
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        if (value is ResponseUsage usage)
        {
            JsonSerializer.Serialize(writer, usage, options);
            return;
        }

        try
        {
            var raw = value is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(value, value.GetType(), options);
            var normalized = raw.ValueKind == JsonValueKind.Object
                ? raw.Deserialize<ResponseUsage>(options)
                : null;

            if (normalized is not null)
            {
                JsonSerializer.Serialize(writer, normalized, options);
                return;
            }
        }
        catch (JsonException)
        {
            // Fall through for unusual legacy values.
        }

        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
