using System.Text.Json.Serialization;

namespace AIHappey.Responses.Streaming;

public class ResponseCreated : ResponseStreamPart
{
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }

    [JsonPropertyName("response")]
    public ResponseResult Response { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.created";

}

