using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Gladia;

/// <summary>
/// Provider-specific options for Gladia pre-recorded transcription (POST /v2/pre-recorded).
/// Maps 1:1 to non-deprecated InitTranscriptionRequest fields.
/// </summary>
public sealed class GladiaTranscriptionProviderMetadata
{
    [JsonPropertyName("custom_vocabulary")]
    public JsonElement? CustomVocabulary { get; set; } // boolean or array

    [JsonPropertyName("custom_vocabulary_config")]
    public JsonElement? CustomVocabularyConfig { get; set; }

    [JsonPropertyName("callback")]
    public bool? Callback { get; set; }

    [JsonPropertyName("callback_config")]
    public JsonElement? CallbackConfig { get; set; }

    [JsonPropertyName("subtitles")]
    public bool? Subtitles { get; set; }

    [JsonPropertyName("subtitles_config")]
    public JsonElement? SubtitlesConfig { get; set; }

    [JsonPropertyName("diarization")]
    public bool? Diarization { get; set; }

    [JsonPropertyName("diarization_config")]
    public JsonElement? DiarizationConfig { get; set; }

    [JsonPropertyName("translation")]
    public bool? Translation { get; set; }

    [JsonPropertyName("translation_config")]
    public JsonElement? TranslationConfig { get; set; }

    [JsonPropertyName("summarization")]
    public bool? Summarization { get; set; }

    [JsonPropertyName("summarization_config")]
    public JsonElement? SummarizationConfig { get; set; }

    [JsonPropertyName("moderation")]
    public bool? Moderation { get; set; }

    [JsonPropertyName("named_entity_recognition")]
    public bool? NamedEntityRecognition { get; set; }

    [JsonPropertyName("chapterization")]
    public bool? Chapterization { get; set; }

    [JsonPropertyName("name_consistency")]
    public bool? NameConsistency { get; set; }

    [JsonPropertyName("custom_spelling")]
    public bool? CustomSpelling { get; set; }

    [JsonPropertyName("custom_spelling_config")]
    public JsonElement? CustomSpellingConfig { get; set; }

    [JsonPropertyName("structured_data_extraction")]
    public bool? StructuredDataExtraction { get; set; }

    [JsonPropertyName("structured_data_extraction_config")]
    public JsonElement? StructuredDataExtractionConfig { get; set; }

    [JsonPropertyName("sentiment_analysis")]
    public bool? SentimentAnalysis { get; set; }

    [JsonPropertyName("audio_to_llm")]
    public bool? AudioToLlm { get; set; }

    [JsonPropertyName("audio_to_llm_config")]
    public JsonElement? AudioToLlmConfig { get; set; }

    [JsonPropertyName("custom_metadata")]
    public JsonElement? CustomMetadata { get; set; }

    [JsonPropertyName("sentences")]
    public bool? Sentences { get; set; }

    [JsonPropertyName("display_mode")]
    public bool? DisplayMode { get; set; }

    [JsonPropertyName("punctuation_enhanced")]
    public bool? PunctuationEnhanced { get; set; }

    [JsonPropertyName("language_config")]
    public JsonElement? LanguageConfig { get; set; }
}
