using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.DeepInfra;

public sealed class DeepInfraZyphraSpeechProviderMetadata
{
    /// <summary>
    /// Output format for the speech. Allowed values: mp3, opus, flac, wav, pcm.
    /// </summary>
    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("preset_voice")]
    public string? PresetVoice { get; set; } //american_female american_male british_female british_male random

    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }

    [JsonPropertyName("speaker_rate")]
    public float? SpeakerRate { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }
}
