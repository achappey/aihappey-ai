using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Gladia;

/// <summary>
/// Provider-specific options for Gladia realtime transcription.
/// Maps 1:1 to realtime config fields.
/// </summary>
public sealed class GladiaRealtimeProviderMetadata
{
    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; } = "wav/pcm"; // wav/pcm | wav/alaw | wav/ulaw

    [JsonPropertyName("bit_depth")]
    public int? BitDepth { get; set; } = 16; // 8 | 16 | 24 | 32

    [JsonPropertyName("sample_rate")]
    public int? SampleRate { get; set; } = 16000; // 8000 | 16000 | 32000 | 44100 | 48000

    [JsonPropertyName("channels")]
    public int? Channels { get; set; } = 1; // 1..8

    [JsonPropertyName("custom_metadata")]
    public Dictionary<string, object>? CustomMetadata { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; } = "solaria-1";

    [JsonPropertyName("endpointing")]
    public double? Endpointing { get; set; } = 0.05; // 0.01..10

    [JsonPropertyName("maximum_duration_without_endpointing")]
    public double? MaximumDurationWithoutEndpointing { get; set; } = 5; // 5..60

    [JsonPropertyName("language_config")]
    public LanguageConfig? LanguageConfig { get; set; }

    [JsonPropertyName("pre_processing")]
    public PreProcessingConfig? PreProcessing { get; set; }

    [JsonPropertyName("realtime_processing")]
    public RealtimeProcessingConfig? RealtimeProcessing { get; set; }

    [JsonPropertyName("post_processing")]
    public PostProcessingConfig? PostProcessing { get; set; }
}

public sealed class LanguageConfig
{
    [JsonPropertyName("languages")]
    public string[]? Languages { get; set; } // ISO codes

    [JsonPropertyName("code_switching")]
    public bool? CodeSwitching { get; set; } = false;
}

public sealed class PreProcessingConfig
{
    [JsonPropertyName("audio_enhancer")]
    public bool? AudioEnhancer { get; set; } = false;

    [JsonPropertyName("speech_threshold")]
    public double? SpeechThreshold { get; set; } = 0.6; // 0..1
}

public sealed class RealtimeProcessingConfig
{
    [JsonPropertyName("custom_vocabulary")]
    public bool? CustomVocabulary { get; set; } = false;

    [JsonPropertyName("custom_vocabulary_config")]
    public CustomVocabularyConfig? CustomVocabularyConfig { get; set; }

    [JsonPropertyName("custom_spelling")]
    public bool? CustomSpelling { get; set; } = false;

    [JsonPropertyName("custom_spelling_config")]
    public object? CustomSpellingConfig { get; set; }

    [JsonPropertyName("translation")]
    public bool? Translation { get; set; } = false;

    [JsonPropertyName("translation_config")]
    public object? TranslationConfig { get; set; }

    [JsonPropertyName("named_entity_recognition")]
    public bool? NamedEntityRecognition { get; set; } = false;

    [JsonPropertyName("sentiment_analysis")]
    public bool? SentimentAnalysis { get; set; } = false;
}

public sealed class CustomVocabularyConfig
{
    [JsonPropertyName("vocabulary")]
    public List<CustomVocabularyItem>? Vocabulary { get; set; } // string OR object

    [JsonPropertyName("default_intensity")]
    public double? DefaultIntensity { get; set; } = 0.5; // 0..1
}

/// <summary>
/// Supports either a raw string entry or a structured object.
/// When serialized, string-only entries should be emitted as strings.
/// </summary>
public sealed class CustomVocabularyItem
{
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("intensity")]
    public double? Intensity { get; set; } // 0..1

    [JsonPropertyName("pronunciations")]
    public string[]? Pronunciations { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; } // ISO code
}

public sealed class PostProcessingConfig
{
    [JsonPropertyName("summarization")]
    public bool? Summarization { get; set; } = false;

    [JsonPropertyName("summarization_config")]
    public SummarizationConfig? SummarizationConfig { get; set; }

    [JsonPropertyName("chapterization")]
    public bool? Chapterization { get; set; } = false;
}

public sealed class SummarizationConfig
{
    [JsonPropertyName("type")]
    public string? Type { get; set; } = "general"; // general | bullet_points | concise
}
