using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.AssemblyAI;

/// <summary>
/// Provider-specific options for AssemblyAI transcription (POST /v2/transcript).
///
/// IMPORTANT: Do not add deprecated properties (e.g. prompt, custom_topics, topics, speech_model, speed_boost, etc.).
/// </summary>
public sealed class AssemblyAITranscriptionProviderMetadata
{
    // ----- Media trimming -----
    [JsonPropertyName("audio_start_from")]
    public int? AudioStartFrom { get; set; } // ms

    [JsonPropertyName("audio_end_at")]
    public int? AudioEndAt { get; set; } // ms

    // ----- Language -----
    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; set; } // e.g. en_us

    [JsonPropertyName("language_detection")]
    public bool? LanguageDetection { get; set; }

    [JsonPropertyName("language_confidence_threshold")]
    public double? LanguageConfidenceThreshold { get; set; }

    // ----- Formatting / output -----
    [JsonPropertyName("punctuate")]
    public bool? Punctuate { get; set; }

    [JsonPropertyName("format_text")]
    public bool? FormatText { get; set; }

    [JsonPropertyName("disfluencies")]
    public bool? Disfluencies { get; set; }

    // ----- Channels / diarization -----
    [JsonPropertyName("multichannel")]
    public bool? Multichannel { get; set; }

    [JsonPropertyName("speaker_labels")]
    public bool? SpeakerLabels { get; set; }

    [JsonPropertyName("speakers_expected")]
    public int? SpeakersExpected { get; set; }

    // ----- Enrichments -----
    [JsonPropertyName("auto_chapters")]
    public bool? AutoChapters { get; set; }

    [JsonPropertyName("auto_highlights")]
    public bool? AutoHighlights { get; set; }

    [JsonPropertyName("entity_detection")]
    public bool? EntityDetection { get; set; }

    [JsonPropertyName("sentiment_analysis")]
    public bool? SentimentAnalysis { get; set; }

    [JsonPropertyName("iab_categories")]
    public bool? IabCategories { get; set; }

    // ----- Safety / profanity / PII -----
    [JsonPropertyName("filter_profanity")]
    public bool? FilterProfanity { get; set; }

    [JsonPropertyName("content_safety")]
    public bool? ContentSafety { get; set; }

    [JsonPropertyName("content_safety_confidence")]
    public int? ContentSafetyConfidence { get; set; } // 25-100

    [JsonPropertyName("redact_pii")]
    public bool? RedactPii { get; set; }

    [JsonPropertyName("redact_pii_audio")]
    public bool? RedactPiiAudio { get; set; }

    [JsonPropertyName("redact_pii_audio_quality")]
    public string? RedactPiiAudioQuality { get; set; } // mp3 | wav

    [JsonPropertyName("redact_pii_policies")]
    public IEnumerable<string>? RedactPiiPolicies { get; set; }

    [JsonPropertyName("redact_pii_sub")]
    public string? RedactPiiSub { get; set; } // entity_name | hash

    // ----- Summarization -----
    [JsonPropertyName("summarization")]
    public bool? Summarization { get; set; }

    [JsonPropertyName("summary_model")]
    public string? SummaryModel { get; set; } // informative | conversational | catchy

    [JsonPropertyName("summary_type")]
    public string? SummaryType { get; set; } // bullets | bullets_verbose | gist | headline | paragraph

    // ----- Other quality controls -----
    [JsonPropertyName("speech_threshold")]
    public double? SpeechThreshold { get; set; } // [0..1]

    [JsonPropertyName("keyterms_prompt")]
    public IEnumerable<string>? KeytermsPrompt { get; set; }

    [JsonPropertyName("custom_spelling")]
    public IEnumerable<AssemblyAICustomSpelling>? CustomSpelling { get; set; }
}

public sealed class AssemblyAICustomSpelling
{
    [JsonPropertyName("from")]
    public IEnumerable<string>? From { get; set; }

    [JsonPropertyName("to")]
    public string? To { get; set; }
}