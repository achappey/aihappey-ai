using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Noiz;

/// <summary>
/// Provider-specific options for Noiz text-to-speech.
/// These fields map directly to the Noiz multipart request shape.
/// </summary>
public sealed class NoizSpeechProviderMetadata
{
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }

    [JsonPropertyName("quality_preset")]
    public int? QualityPreset { get; set; }

    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    [JsonPropertyName("duration")]
    public float? Duration { get; set; }

    [JsonPropertyName("target_lang")]
    public string? TargetLang { get; set; }

    [JsonPropertyName("similarity_enh")]
    public bool? SimilarityEnh { get; set; }

    [JsonPropertyName("emo")]
    public string? Emo { get; set; }

    [JsonPropertyName("trim_silence")]
    public bool? TrimSilence { get; set; }

    [JsonPropertyName("save_voice")]
    public bool? SaveVoice { get; set; }
}
