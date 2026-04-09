using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Messages;

public sealed class MessagesContentJsonConverter : JsonConverter<MessagesContent>
{
    public override MessagesContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        return root.ValueKind switch
        {
            JsonValueKind.String => new MessagesContent(root.GetString() ?? string.Empty),
            JsonValueKind.Array => new MessagesContent(root.Deserialize<List<MessageContentBlock>>(options) ?? []),
            JsonValueKind.Object => new MessagesContent(root.Clone()),
            JsonValueKind.Null or JsonValueKind.Undefined => new MessagesContent(),
            _ => throw new JsonException($"Unsupported messages content shape '{root.ValueKind}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, MessagesContent value, JsonSerializerOptions options)
    {
        if (value.IsText)
        {
            writer.WriteStringValue(value.Text);
            return;
        }

        if (value.IsBlocks)
        {
            JsonSerializer.Serialize(writer, value.Blocks, options);
            return;
        }

        if (value.IsRaw)
        {
            value.Raw!.Value.WriteTo(writer);
            return;
        }

        writer.WriteNullValue();
    }
}
