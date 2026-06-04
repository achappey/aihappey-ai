using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.HumeAI;

/// <summary>
/// Provider options for HumeAI Octave text-to-speech.
/// Consumed via <c>providerOptions.humeai</c>.
/// </summary>
public sealed class HumeAISpeechProviderMetadata
{
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }

    [JsonPropertyName("voice_name")]
    public string? VoiceName { get; set; }

    [JsonPropertyName("voice_provider")]
    public string? VoiceProvider { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("num_generations")]
    public int? NumGenerations { get; set; }

    [JsonPropertyName("split_utterances")]
    public bool? SplitUtterances { get; set; }

    [JsonPropertyName("strip_headers")]
    public bool? StripHeaders { get; set; }

    [JsonPropertyName("include_timestamp_types")]
    public IReadOnlyList<string>? IncludeTimestampTypes { get; set; }

    [JsonPropertyName("context_generation_id")]
    public string? ContextGenerationId { get; set; }

    [JsonPropertyName("context_utterances")]
    public IReadOnlyList<HumeAIUtteranceMetadata>? ContextUtterances { get; set; }

    [JsonPropertyName("trailing_silence")]
    public double? TrailingSilence { get; set; }

    [JsonPropertyName("instant_mode")]
    public bool? InstantMode { get; set; }
}

public sealed class HumeAIUtteranceMetadata
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("speed")]
    public double? Speed { get; set; }

    [JsonPropertyName("trailing_silence")]
    public double? TrailingSilence { get; set; }

    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }

    [JsonPropertyName("voice_name")]
    public string? VoiceName { get; set; }

    [JsonPropertyName("voice_provider")]
    public string? VoiceProvider { get; set; }
}
