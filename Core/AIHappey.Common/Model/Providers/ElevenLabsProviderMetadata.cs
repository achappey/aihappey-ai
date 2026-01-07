using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public class ElevenLabsSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    /// <summary>
    /// Query-string `output_format` e.g. `mp3_44100_128`.
    /// </summary>
    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("enable_logging")]
    public bool? EnableLogging { get; set; }

    [JsonPropertyName("optimize_streaming_latency")]
    public int? OptimizeStreamingLatency { get; set; }

    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; set; }

    [JsonPropertyName("seed")]
    public uint? Seed { get; set; }

    [JsonPropertyName("voice_settings")]
    public ElevenLabsVoiceSettings? VoiceSettings { get; set; }
}

public class ElevenLabsVoiceSettings
{
    [JsonPropertyName("stability")]
    public float? Stability { get; set; }

    [JsonPropertyName("similarity_boost")]
    public float? SimilarityBoost { get; set; }

    [JsonPropertyName("style")]
    public float? Style { get; set; }

    [JsonPropertyName("use_speaker_boost")]
    public bool? UseSpeakerBoost { get; set; }
}

public class ElevenLabsTranscriptionProviderMetadata
{
    /// <summary>
    /// Form field `model_id` e.g. `scribe_v1`.
    /// </summary>
    [JsonPropertyName("model_id")]
    public string? ModelId { get; set; }

    [JsonPropertyName("enable_logging")]
    public bool? EnableLogging { get; set; }

    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; set; }

    [JsonPropertyName("tag_audio_events")]
    public bool? TagAudioEvents { get; set; }

    [JsonPropertyName("num_speakers")]
    public int? NumSpeakers { get; set; }

    [JsonPropertyName("timestamps_granularity")]
    public string? TimestampsGranularity { get; set; } // none | word | character

    [JsonPropertyName("diarize")]
    public bool? Diarize { get; set; }

    [JsonPropertyName("diarization_threshold")]
    public float? DiarizationThreshold { get; set; }

    [JsonPropertyName("file_format")]
    public string? FileFormat { get; set; } // pcm_s16le_16 | other

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("use_multi_channel")]
    public bool? UseMultiChannel { get; set; }
}

/// <summary>
/// Chat/provider-level options (ElevenLabs currently only supports speech + transcription in our app).
/// Kept for schema discovery parity with other providers.
/// </summary>
public class ElevenLabsProviderMetadata
{
}

