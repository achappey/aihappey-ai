using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

/// <summary>AI SDK TranscriptionModelV4 streaming request.</summary>
public sealed class StreamingTranscriptionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    /// <summary>The complete audio file encoded as plain base64.</summary>
    [JsonPropertyName("audio")]
    public string Audio { get; set; } = null!;

    [JsonPropertyName("inputAudioFormat")]
    public AudioFormat InputAudioFormat { get; set; } = null!;

    [JsonPropertyName("providerOptions")]
    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }

    [JsonPropertyName("includeRawChunks")]
    public bool? IncludeRawChunks { get; set; }
}

public sealed class AudioFormat
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = null!;

    [JsonPropertyName("rate")]
    public int? Rate { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TranscriptionStreamStartPart), "stream-start")]
[JsonDerivedType(typeof(TranscriptionDeltaPart), "transcript-delta")]
[JsonDerivedType(typeof(TranscriptionPartialPart), "transcript-partial")]
[JsonDerivedType(typeof(TranscriptionFinalPart), "transcript-final")]
[JsonDerivedType(typeof(TranscriptionResponseMetadataPart), "response-metadata")]
[JsonDerivedType(typeof(TranscriptionFinishPart), "finish")]
[JsonDerivedType(typeof(TranscriptionRawPart), "raw")]
[JsonDerivedType(typeof(TranscriptionErrorPart), "error")]
public abstract class StreamingTranscriptionPart;

public sealed class TranscriptionStreamStartPart : StreamingTranscriptionPart
{
    [JsonPropertyName("warnings")]
    public IEnumerable<object> Warnings { get; set; } = [];
}

public sealed class TranscriptionDeltaPart : StreamingTranscriptionPart
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("delta")]
    public string Delta { get; set; } = null!;

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }
}

public sealed class TranscriptionPartialPart : StreamingTranscriptionPart
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

    [JsonPropertyName("startSecond")]
    public double? StartSecond { get; set; }

    [JsonPropertyName("durationInSeconds")]
    public double? DurationInSeconds { get; set; }

    [JsonPropertyName("channelIndex")]
    public int? ChannelIndex { get; set; }

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }
}

public sealed class TranscriptionFinalPart : StreamingTranscriptionPart
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

    [JsonPropertyName("startSecond")]
    public double? StartSecond { get; set; }

    [JsonPropertyName("endSecond")]
    public double? EndSecond { get; set; }

    [JsonPropertyName("channelIndex")]
    public int? ChannelIndex { get; set; }

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }
}

public sealed class TranscriptionResponseMetadataPart : StreamingTranscriptionPart
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

public sealed class TranscriptionFinishPart : StreamingTranscriptionPart
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

    [JsonPropertyName("segments")]
    public IEnumerable<TranscriptionSegment> Segments { get; set; } = [];

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("durationInSeconds")]
    public double? DurationInSeconds { get; set; }

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }
}

public sealed class TranscriptionRawPart : StreamingTranscriptionPart
{
    [JsonPropertyName("rawValue")]
    public object? RawValue { get; set; }
}

public sealed class TranscriptionErrorPart : StreamingTranscriptionPart
{
    [JsonPropertyName("error")]
    public object? Error { get; set; }
}
