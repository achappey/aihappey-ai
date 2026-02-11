using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.RekaAI;

/// <summary>
/// Provider-specific options for Reka speech transcription or translation.
/// Endpoint: <c>POST /v1/transcription_or_translation</c>.
/// </summary>
public sealed class RekaAITranscriptionProviderMetadata
{
    /// <summary>
    /// Audio sampling rate in Hz.
    /// Default: <c>16000</c>.
    /// </summary>
    [JsonPropertyName("sampling_rate")]
    public int? SamplingRate { get; set; }

    /// <summary>
    /// Target language for translation mode.
    /// Allowed by Reka docs: french, spanish, japanese, chinese, korean, italian, portuguese, german.
    /// </summary>
    [JsonPropertyName("target_language")]
    public string? TargetLanguage { get; set; }

    /// <summary>
    /// Set <c>true</c> to request translation mode.
    /// </summary>
    [JsonPropertyName("is_translate")]
    public bool? IsTranslate { get; set; }

    /// <summary>
    /// Set <c>true</c> to request translated speech audio output.
    /// </summary>
    [JsonPropertyName("return_translation_audio")]
    public bool? ReturnTranslationAudio { get; set; }

    /// <summary>
    /// Decoding temperature.
    /// </summary>
    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    /// <summary>
    /// Maximum number of generated tokens.
    /// Default: <c>1024</c>.
    /// </summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }
}

