using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.ElevenLabs;

public sealed class ElevenLabsSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    /// <summary>
    /// Query-string <c>output_format</c> e.g. <c>mp3_44100_128</c>.
    /// </summary>
    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("enable_logging")]
    public bool? EnableLogging { get; set; }

    [JsonPropertyName("seed")]
    public uint? Seed { get; set; }

    [JsonPropertyName("voice_settings")]
    public ElevenLabsVoiceSettings? VoiceSettings { get; set; }

    [JsonPropertyName("previous_text")]
    public string? PreviousText { get; set; }

    [JsonPropertyName("next_text")]
    public string? NextText { get; set; }

    [JsonPropertyName("apply_text_normalization")]
    public string? ApplyTextNormalization { get; set; } // auto | on | off

    [JsonPropertyName("apply_language_text_normalization")]
    public bool? ApplyLanguageTextNormalization { get; set; } 
}

