using System.Text.Json.Serialization;

namespace AIHappey.Core.Models;

/// <summary>
/// Common root contract for all JSON transcription responses.
///
/// The concrete response type is determined by response_format:
/// - json            => OpenAITranscriptionResponse
/// - diarized_json   => OpenAITranscriptionDiarizedResponse
/// - verbose_json    => OpenAITranscriptionVerboseResponse
/// </summary>
[JsonDerivedType(typeof(OpenAITranscriptionResponse))]
[JsonDerivedType(typeof(OpenAITranscriptionDiarizedResponse))]
[JsonDerivedType(typeof(OpenAITranscriptionVerboseResponse))]
public interface IOpenAITranscriptionResponse
{
    [JsonPropertyName("text")]
    string Text { get; }
}

/// <summary>
/// Standard JSON transcription response.
/// </summary>
public sealed class OpenAITranscriptionResponse : IOpenAITranscriptionResponse
{
    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAITranscriptionLogprob[]? Logprobs { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAITranscriptionUsage? Usage { get; set; }
}

/// <summary>
/// Diarized JSON transcription response.
/// </summary>
public sealed class OpenAITranscriptionDiarizedResponse : IOpenAITranscriptionResponse
{
    [JsonPropertyName("task")]
    public string Task => "transcribe";

    [JsonPropertyName("duration")]
    public required double Duration { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("segments")]
    public required OpenAITranscriptionTextSegment[] Segments { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAITranscriptionUsage? Usage { get; set; }
}

/// <summary>
/// Verbose JSON transcription response.
/// </summary>
public sealed class OpenAITranscriptionVerboseResponse : IOpenAITranscriptionResponse
{
    private OpenAITranscriptionUsage? usage;

    [JsonPropertyName("task")]
    public string Task => "transcribe";

    [JsonPropertyName("language")]
    public required string Language { get; set; }

    [JsonPropertyName("duration")]
    public required double Duration { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("segments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAITranscriptionSegment[]? Segments { get; set; }

    [JsonPropertyName("words")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAITranscriptionWord[]? Words { get; set; }

    /// <summary>
    /// OpenAI verbose_json responses only use duration-based usage.
    /// The base type is retained so the "type" discriminator is serialized.
    /// </summary>
    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAITranscriptionUsage? Usage
    {
        get => usage;
        set
        {
            if (value is not null and not OpenAITranscriptionDurationUsage)
            {
                throw new ArgumentException(
                    "Verbose transcription responses only support duration usage.",
                    nameof(value));
            }

            usage = value;
        }
    }
}

/// <summary>
/// Log probability information for one transcription token.
/// </summary>
public sealed class OpenAITranscriptionLogprob
{
    [JsonPropertyName("token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; set; }

    // Intentionally int[], not byte[].
    // System.Text.Json serializes byte[] as a Base64 string.
    [JsonPropertyName("bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? Bytes { get; set; }

    [JsonPropertyName("logprob")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Logprob { get; set; }
}

/// <summary>
/// Base usage type.
///
/// OpenAI identifies the concrete usage variant through the "type" property.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OpenAITranscriptionTokenUsage), "tokens")]
[JsonDerivedType(typeof(OpenAITranscriptionDurationUsage), "duration")]
public abstract class OpenAITranscriptionUsage;

/// <summary>
/// Usage for transcription models billed by tokens.
/// </summary>
public sealed class OpenAITranscriptionTokenUsage : OpenAITranscriptionUsage
{
    [JsonPropertyName("input_tokens")]
    public required long InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public required long OutputTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public required long TotalTokens { get; set; }

    [JsonPropertyName("input_token_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAITranscriptionInputTokenDetails? InputTokenDetails { get; set; }
}

/// <summary>
/// Breakdown of billed transcription input tokens.
/// </summary>
public sealed class OpenAITranscriptionInputTokenDetails
{
    [JsonPropertyName("audio_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AudioTokens { get; set; }

    [JsonPropertyName("text_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TextTokens { get; set; }
}

/// <summary>
/// Usage for transcription models billed by audio duration.
/// </summary>
public sealed class OpenAITranscriptionDurationUsage : OpenAITranscriptionUsage
{
    [JsonPropertyName("seconds")]
    public required double Seconds { get; set; }
}

/// <summary>
/// A diarized transcription segment.
///
/// This type is used both inside diarized_json responses and as the
/// transcript.text.segment streaming event.
/// </summary>
public sealed class OpenAITranscriptionTextSegment
    : IOpenAITranscriptionStreamEvent
{
    [JsonPropertyName("type")]
    public string Type => "transcript.text.segment";

    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("start")]
    public required double Start { get; set; }

    [JsonPropertyName("end")]
    public required double End { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("speaker")]
    public required string Speaker { get; set; }
}

/// <summary>
/// Detailed segment returned in a verbose_json response.
/// </summary>
public sealed class OpenAITranscriptionSegment
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("seek")]
    public required int Seek { get; set; }

    [JsonPropertyName("start")]
    public required double Start { get; set; }

    [JsonPropertyName("end")]
    public required double End { get; set; }

    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("tokens")]
    public required int[] Tokens { get; set; }

    [JsonPropertyName("temperature")]
    public required float Temperature { get; set; }

    [JsonPropertyName("avg_logprob")]
    public required float AverageLogprob { get; set; }

    [JsonPropertyName("compression_ratio")]
    public required float CompressionRatio { get; set; }

    [JsonPropertyName("no_speech_prob")]
    public required float NoSpeechProbability { get; set; }
}

/// <summary>
/// Word-level timestamp returned in a verbose_json response.
/// </summary>
public sealed class OpenAITranscriptionWord
{
    [JsonPropertyName("word")]
    public required string Word { get; set; }

    [JsonPropertyName("start")]
    public required double Start { get; set; }

    [JsonPropertyName("end")]
    public required double End { get; set; }
}