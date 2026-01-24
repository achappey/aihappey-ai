

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Responses;

public sealed class ResponseMessageContentJsonConverter : JsonConverter<ResponseMessageContent>
{
    public override ResponseMessageContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString() ?? "";
            return new ResponseMessageContent(s);
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var parts = JsonSerializer.Deserialize<List<ResponseContentPart>>(ref reader, options);
            return parts is null ? new ResponseMessageContent(Array.Empty<ResponseContentPart>()) : new ResponseMessageContent(parts);
        }

        throw new JsonException($"Invalid message.content token: {reader.TokenType}. Expected string or array.");
    }

    public override void Write(Utf8JsonWriter writer, ResponseMessageContent value, JsonSerializerOptions options)
    {
        if (value.IsText)
        {
            writer.WriteStringValue(value.Text);
            return;
        }

        if (value.IsParts)
        {
            JsonSerializer.Serialize(writer, value.Parts, options);
            return;
        }

        writer.WriteStringValue("");
    }
}