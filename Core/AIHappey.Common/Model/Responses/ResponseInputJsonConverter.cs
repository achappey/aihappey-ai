
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Responses;

public sealed class ResponseInputJsonConverter : JsonConverter<ResponseInput>
{
    public override ResponseInput? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            return s is null ? null : new ResponseInput(s);
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var items = JsonSerializer.Deserialize<List<ResponseInputItem>>(ref reader, options);
            return items is null ? null : new ResponseInput(items);
        }

        throw new JsonException($"Invalid `input` token: {reader.TokenType}. Expected string or array.");
    }

    public override void Write(Utf8JsonWriter writer, ResponseInput value, JsonSerializerOptions options)
    {
        if (value.IsText)
        {
            writer.WriteStringValue(value.Text);
            return;
        }

        if (value.IsItems)
        {
            JsonSerializer.Serialize(writer, value.Items, options);
            return;
        }

        // Should never happen
        writer.WriteNullValue();
    }
}