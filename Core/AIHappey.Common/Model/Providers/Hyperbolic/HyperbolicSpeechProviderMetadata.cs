using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Hyperbolic;

public class HyperbolicSpeechProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("sdp_ratio")]
    public float? SdpRatio { get; set; }

    [JsonPropertyName("noise_scale")]
    public float? NoiseScale { get; set; }

    [JsonPropertyName("noise_scale_w")]
    public float? NoiseScaleW { get; set; }

    [JsonPropertyName("speed")]
    public float? Speed { get; set; }
}

