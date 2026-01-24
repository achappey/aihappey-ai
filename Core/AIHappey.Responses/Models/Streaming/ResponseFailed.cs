using System.Text.Json.Serialization;

namespace AIHappey.Responses.Streaming;

public class ResponseFailed : ResponseStreamPart
{
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }

    [JsonPropertyName("response")]
    public ResponseResult Response { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.failed";

}

