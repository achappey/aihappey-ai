using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Responses.Streaming;

public class ResponseInProgress : ResponseStreamPart
{
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }

    [JsonPropertyName("response")]
    public ResponseResult Response { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.in_progress";

}

