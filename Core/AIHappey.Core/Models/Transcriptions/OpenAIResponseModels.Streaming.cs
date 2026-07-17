using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Models;

public interface IOpenAITranscriptionStreamEvent
{
    [JsonPropertyName("type")]
    string Type { get; }
}

public class OpenAITranscriptionTextDelta : IOpenAITranscriptionStreamEvent
{
    [JsonPropertyName("type")]
    public string Type => "transcript.text.delta";

    [JsonPropertyName("delta")]
    public required string Delta { get; set; }

    [JsonPropertyName("logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAITranscriptionLogprob[]? Logprobs { get; set; }

    [JsonPropertyName("segment_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SegmentId { get; set; }
}

public class OpenAITranscriptionTextDone : IOpenAITranscriptionStreamEvent
{
    [JsonPropertyName("type")]
    public string Type => "transcript.text.done";

    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAITranscriptionLogprob[]? Logprobs { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Usage { get; set; }
}

