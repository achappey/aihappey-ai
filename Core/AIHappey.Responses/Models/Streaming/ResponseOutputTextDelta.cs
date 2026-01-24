using System.Text.Json.Serialization;

namespace AIHappey.Responses.Streaming;

public class ResponseOutputTextDelta : ResponseStreamPart
{
    [JsonPropertyName("delta")]
    public string Delta { get; init; } = default!;

    [JsonPropertyName("item_id")]
    public string ItemId { get; init; } = default!;

    [JsonPropertyName("content_index")]
    public int ContentIndex { get; init; }

    [JsonPropertyName("output_index")]
    public int Outputindex { get; init; }

    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; init; }

    [JsonPropertyName("type")]
    public override string Type { get; init; } = "response.output_text.delta";

}

