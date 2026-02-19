using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.APIpie;

public sealed class APIpieSpeechProviderMetadata
{
    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("voiceSettings")]
    public APIpieVoiceSettings? VoiceSettings { get; set; }

    [JsonPropertyName("responseFormat")]
    public string? ResponseFormat { get; set; }
}

public sealed class APIpieVoiceSettings
{
    [JsonPropertyName("stability")]
    public double? Stability { get; set; }

    [JsonPropertyName("similarity_boost")]
    public double? SimilarityBoost { get; set; }

    [JsonPropertyName("style")]
    public double? Style { get; set; }

    [JsonPropertyName("use_speaker_boost")]
    public bool? UseSpeakerBoost { get; set; }
}

