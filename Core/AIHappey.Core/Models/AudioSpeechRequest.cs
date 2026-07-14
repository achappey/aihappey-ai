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
    public string? Voice { get; set; }

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    [JsonPropertyName("stream_format")]
    public string? StreamFormat { get; set; }
}


public class AudioSpeechStreamDelta
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "speech.audio.delta";

    [JsonPropertyName("audio")]
    public required string Audio { get; set; }

}


public class AudioSpeechStreamDone
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "speech.audio.done";

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