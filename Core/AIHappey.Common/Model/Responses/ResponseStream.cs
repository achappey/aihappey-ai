using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Responses;

[JsonConverter(typeof(ResponseStreamConverter))]
public abstract class ResponseStreamPart
{
    public abstract string Type { get; init; }
}

public class ResponseOutputTextDelta : ResponseStreamPart
{
    [JsonPropertyName("delta")]
    public string Delta { get; init; } = default!;

    [JsonPropertyName("item_id")]
    public string ItemId { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.output_text.delta";

}

public class ResponseStreamConverter : JsonConverter<ResponseStreamPart>
{
    private static readonly Dictionary<string, Type> PartTypeMap = new()
    {
        ["response.output_text.delta"] = typeof(ResponseOutputTextDelta),
    };

    public static ResponseStreamPart DeserializePart(string typeProp, JsonElement root, JsonSerializerOptions options)
    {
        if (PartTypeMap.TryGetValue(typeProp, out var targetType))
        {
            var part = JsonSerializer.Deserialize(root.GetRawText(), targetType, options)
                       ?? throw new JsonException($"Failed to deserialize type: {typeProp}");

            // Cast to UIMessagePart, will throw if not correct type
            return part as ResponseStreamPart
                   ?? throw new JsonException($"Deserialized type is not a ResponseStreamPart: {typeProp}");
        }

        throw new JsonException($"Unknown ResponseStreamPart type discriminator: {typeProp}");
    }

    public override ResponseStreamPart? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        string typeProp = root.GetProperty("type").GetString() ?? throw new ArgumentException();

        return DeserializePart(typeProp!, root, options);
    }

    public override void Write(Utf8JsonWriter writer, ResponseStreamPart value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
    }
}