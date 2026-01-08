using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class ContainerUnionConverter : JsonConverter<ContainerUnion>
{
    public override ContainerUnion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return default;

        if (reader.TokenType == JsonTokenType.String)
            return new ContainerUnion(reader.GetString() ?? string.Empty);

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var obj = JsonSerializer.Deserialize<CodeInterpreterContainer>(ref reader, options) ?? new CodeInterpreterContainer();
            return new ContainerUnion(obj);
        }

        throw new JsonException("Expected string or object for 'container'.");
    }

    public override void Write(Utf8JsonWriter writer, ContainerUnion value, JsonSerializerOptions options)
    {
        if (value.IsString)
        {
            writer.WriteStringValue(value.String);
            return;
        }

        if (value.IsObject)
        {
            JsonSerializer.Serialize(writer, value.Object, options);
            return;
        }

        writer.WriteNullValue();
    }
}

