using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Responses.Streaming;

public class ResponseError : ResponseStreamPart
{
    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = default!;

    [JsonPropertyName("param")]
    public string Param { get; init; } = default!;

    [JsonPropertyName("code")]
    public string Code { get; init; } = default!;

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "error";

}

