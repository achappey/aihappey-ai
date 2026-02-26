using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Cartesia;

/// <summary>
/// Provider options for Cartesia TTS.
/// Consumed via <c>providerOptions.cartesia</c>.
/// </summary>
public sealed class CartesiaSpeechProviderMetadata
{
    [JsonPropertyName("apiVersion")]
    public string? ApiVersion { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// wav | mp3 | raw
    /// </summary>
    [JsonPropertyName("container")]
    public string? Container { get; set; }

    [JsonPropertyName("sampleRate")]
    public int? SampleRate { get; set; }

    /// <summary>
    /// MP3 only. 32000 | 64000 | 96000 | 128000 | 192000
    /// </summary>
    [JsonPropertyName("bitRate")]
    public int? BitRate { get; set; }

    /// <summary>
    /// raw/wav only. pcm_f32le | pcm_s16le | pcm_mulaw | pcm_alaw
    /// </summary>
    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    /// <summary>
    /// Uses generation_config.speed. Valid for sonic-3 family: 0.6 - 1.5.
    /// </summary>
    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    /// <summary>
    /// Uses generation_config.volume. Valid range: 0.5 - 2.0.
    /// </summary>
    [JsonPropertyName("volume")]
    public float? Volume { get; set; }

    [JsonPropertyName("emotion")]
    public string? Emotion { get; set; }
}

