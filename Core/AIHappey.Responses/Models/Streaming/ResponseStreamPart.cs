using System.Text.Json.Serialization;

namespace AIHappey.Responses.Streaming;

[JsonConverter(typeof(ResponseStreamConverter))]
public abstract class ResponseStreamPart
{
    [JsonIgnore]
    public abstract string Type { get; init; }

    [JsonPropertyName("agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseAgent? Agent { get; init; }
}
