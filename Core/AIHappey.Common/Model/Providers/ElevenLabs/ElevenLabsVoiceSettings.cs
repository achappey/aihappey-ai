using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.ElevenLabs;

public sealed class ElevenLabsVoiceSettings
{
    [JsonPropertyName("stability")]
    public float? Stability { get; set; }

    [JsonPropertyName("similarity_boost")]
    public float? SimilarityBoost { get; set; }

    [JsonPropertyName("style")]
    public float? Style { get; set; }

    [JsonPropertyName("use_speaker_boost")]
    public bool? UseSpeakerBoost { get; set; }
}

