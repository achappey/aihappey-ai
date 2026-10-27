using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

/// <summary>
/// AI SDK SpeechModelV4 streaming request for POST /api/speech/stream.
/// The text input is buffered as an ordered JSON array, not streamed over the wire.
/// </summary>
public sealed class StreamingSpeechRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    /// <summary>Consecutive parts form one script; boundaries may occur mid-word.</summary>
    [JsonPropertyName("text")]
    public StreamingSpeechTextPart[] Text { get; set; } = null!;

    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("outputFormat")]
    public AudioFormat? OutputFormat { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("speed")]
    public double? Speed { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("providerOptions")]
    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }

    [JsonPropertyName("includeRawChunks")]
    public bool? IncludeRawChunks { get; set; }
}

public sealed class StreamingSpeechTextPart
{
    [Required]
    [RegularExpression("^text$")]
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [Required(AllowEmptyStrings = true)]
    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

    /// <summary>Defaults to the request's voice, or the provider's default voice.</summary>
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("providerOptions")]
    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }
}

public sealed class StreamingSpeechUsage
{
    /// <summary>Number of input Unicode code points, if reported by the provider.</summary>
    [JsonPropertyName("inputCharacters")]
    public long? InputCharacters { get; set; }

    [JsonPropertyName("inputTokens")]
    public long? InputTokens { get; set; }

    [JsonPropertyName("outputTokens")]
    public long? OutputTokens { get; set; }

    [JsonPropertyName("raw")]
    public Dictionary<string, JsonElement>? Raw { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SpeechStreamStartPart), "stream-start")]
[JsonDerivedType(typeof(SpeechAudioPart), "audio")]
[JsonDerivedType(typeof(SpeechResponseMetadataPart), "response-metadata")]
[JsonDerivedType(typeof(SpeechFinishPart), "finish")]
[JsonDerivedType(typeof(SpeechRawPart), "raw")]
[JsonDerivedType(typeof(SpeechErrorPart), "error")]
public abstract class StreamingSpeechPart;

public sealed class SpeechStreamStartPart : StreamingSpeechPart
{
    [JsonPropertyName("warnings")]
    public IEnumerable<object> Warnings { get; set; } = [];
}

public sealed class SpeechAudioPart : StreamingSpeechPart
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Plain base64-encoded audio bytes, not a data URL. Chunks are ordered and
    /// need not be standalone files. Empty chunks may carry provider metadata.
    /// </summary>
    [JsonPropertyName("audio")]
    public string Audio { get; set; } = null!;

    [JsonPropertyName("audioFormat")]
    public AudioFormat AudioFormat { get; set; } = null!;

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }
}

public sealed class SpeechResponseMetadataPart : StreamingSpeechPart
{
    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; set; }

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("headers")]
    public IDictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("body")]
    public object? Body { get; set; }
}

/// <summary>Emitted exactly once after all audio chunks on successful completion.</summary>
public sealed class SpeechFinishPart : StreamingSpeechPart
{
    [JsonPropertyName("usage")]
    public StreamingSpeechUsage? Usage { get; set; }

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }
}

public sealed class SpeechRawPart : StreamingSpeechPart
{
    [JsonPropertyName("rawValue")]
    public object? RawValue { get; set; }
}

/// <summary>
/// A non-fatal provider error may be followed by more events. An unrecoverable
/// failure closes the stream without a finish part.
/// </summary>
public sealed class SpeechErrorPart : StreamingSpeechPart
{
    [JsonPropertyName("error")]
    public object? Error { get; set; }
}
