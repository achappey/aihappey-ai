
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Responses;

public sealed class ResponseInputItemJsonConverter : JsonConverter<ResponseInputItem>
{
    public override ResponseInputItem Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // "type" is optional in some docs/implementations, but usually present.
        var type = root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString()
            : null;

        // Fallback heuristic: if it has "role" -> message
        if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase) ||
            (type is null && root.TryGetProperty("role", out _)))
        {
            return root.Deserialize<ResponseInputMessage>(options)
                   ?? throw new JsonException("Could not deserialize message input item.");
        }

        if (string.Equals(type, "item_reference", StringComparison.OrdinalIgnoreCase))
        {
            return root.Deserialize<ResponseItemReference>(options)
                   ?? throw new JsonException("Could not deserialize item_reference.");
        }

        throw new JsonException($"Unknown input item type: '{type ?? "(missing)"}'");
    }

    public override void Write(Utf8JsonWriter writer, ResponseInputItem value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case ResponseInputMessage msg:
                JsonSerializer.Serialize(writer, msg, options);
                return;

            case ResponseItemReference itemRef:
                JsonSerializer.Serialize(writer, itemRef, options);
                return;

            default:
                throw new JsonException($"Unsupported input item: {value.GetType().Name}");
        }
    }
}
