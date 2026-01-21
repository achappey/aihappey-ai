using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

public sealed class FreepikSpeechProviderMetadata
{
    [JsonPropertyName("sound_effects")]
    public SoundEffects? SoundEffects { get; set; }
}

public sealed class SoundEffects
{
    [JsonPropertyName("duration_seconds")]
    public int DurationSeconds { get; set; }

    [JsonPropertyName("prompt_influence")]
    public float? PromptInfluence { get; set; }

    [JsonPropertyName("loop")]
    public bool? Loop { get; set; }

}