using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.RekaAI;

/// <summary>
/// Provider-specific options for transcription implemented through Reka's chat-completions endpoint.
/// </summary>
public sealed class RekaAITranscriptionProviderMetadata
{
    /// <summary>
    /// Additional instructions included after the fixed transcription-only instruction.
    /// </summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    /// <summary>
    /// Requested language. Reka chat transcription does not support this option and returns a warning.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Requested OpenAI timestamp granularities. Reka chat transcription does not return segments and returns a warning.
    /// </summary>
    [JsonPropertyName("timestamp_granularities")]
    public string[]? TimestampGranularities { get; set; }

    /// <summary>
    /// Audio sampling rate in Hz. Retained for backwards compatibility; ignored by chat-completions transcription.
    /// </summary>
    [JsonPropertyName("sampling_rate")]
    public int? SamplingRate { get; set; }

    /// <summary>
    /// Target language for translation mode. Retained for backwards compatibility; translation is unsupported.
    /// </summary>
    [JsonPropertyName("target_language")]
    public string? TargetLanguage { get; set; }

    /// <summary>
    /// Set <c>true</c> to request translation mode. Retained for backwards compatibility; translation is unsupported.
    /// </summary>
    [JsonPropertyName("is_translate")]
    public bool? IsTranslate { get; set; }

    /// <summary>
    /// Set <c>true</c> to request translated speech audio output. Retained for backwards compatibility; translated audio is unsupported.
    /// </summary>
    [JsonPropertyName("return_translation_audio")]
    public bool? ReturnTranslationAudio { get; set; }

    /// <summary>
    /// Decoding temperature.
    /// </summary>
    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    /// <summary>
    /// Maximum number of generated tokens. Retained for backwards compatibility; ignored by chat-completions transcription.
    /// </summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }
}

