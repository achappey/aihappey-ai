using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.ElevenLabs;

public sealed class ElevenLabsTranscriptionProviderMetadata
{
    /// <summary>
    /// Form field <c>model_id</c> e.g. <c>scribe_v1</c>.
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

