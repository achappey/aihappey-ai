using System.Text.Json.Serialization;

namespace AIHappey.Core.Models;

/// <summary>
/// OpenAI compatible request DTO for <c>POST /v1/audio/speech</c>.
/// <para>
/// This JSON shape is a public, contract-locked surface. Do not rename properties, change casing,
/// alter types, or restructure this DTO.
/// </para>
/// </summary>
public class AudioSpeechRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    [JsonPropertyName("input")]
    public string Input { get; set; } = null!;

    [JsonPropertyName("voice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Voice { get; set; }

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }

    [JsonPropertyName("speed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Speed { get; set; }

    [JsonPropertyName("stream_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StreamFormat { get; set; }
}

public interface IAudioSpeechStreamEvent
{
    [JsonPropertyName("type")]
    string Type { get; }
}

public class AudioSpeechStreamDelta : IAudioSpeechStreamEvent
{
    [JsonPropertyName("type")]
    public string Type => "speech.audio.delta";


    [JsonPropertyName("audio")]
    public required string Audio { get; set; }

}


public class AudioSpeechStreamDone : IAudioSpeechStreamEvent
{
    [JsonPropertyName("type")]
    public string Type => "speech.audio.done";

    [JsonPropertyName("usage")]
    public AudioSpeechUsage? Usage { get; set; }

}

public class AudioSpeechUsage
{
    [JsonPropertyName("input_tokens")]
    public int? InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int? OutputTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; set; }

}