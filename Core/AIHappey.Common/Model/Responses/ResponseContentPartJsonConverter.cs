
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Responses;


public sealed class ResponseContentPartJsonConverter : JsonConverter<ResponseContentPart>
{
    public override ResponseContentPart Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            throw new JsonException("Content part is missing required 'type'.");

        var type = typeEl.GetString();

        return type switch
        {
            "input_text" => root.Deserialize<InputTextPart>(options)
                          ?? throw new JsonException("Failed to deserialize input_text."),
            "input_image" => root.Deserialize<InputImagePart>(options)
                          ?? throw new JsonException("Failed to deserialize input_image."),
            "input_file" => root.Deserialize<InputFilePart>(options)
                          ?? throw new JsonException("Failed to deserialize input_file."),
            _ => throw new JsonException($"Unknown content part type: '{type}'")
        };
    }

    public override void Write(Utf8JsonWriter writer, ResponseContentPart value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case InputTextPart t:
                JsonSerializer.Serialize(writer, t, options);
                return;
            case InputImagePart i:
                JsonSerializer.Serialize(writer, i, options);
                return;
            case InputFilePart f:
                JsonSerializer.Serialize(writer, f, options);
                return;
            default:
                throw new JsonException($"Unsupported content part: {value.GetType().Name}");
        }
    }
}
