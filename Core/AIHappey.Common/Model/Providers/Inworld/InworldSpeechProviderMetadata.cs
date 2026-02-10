using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Inworld;

public sealed class InworldSpeechProviderMetadata
{
    /// <summary>
    /// Optional audio configuration for the Inworld TTS API.
    /// </summary>
    [JsonPropertyName("audioConfig")]
    public InworldSpeechAudioConfig? AudioConfig { get; set; }

    /// <summary>
    /// Timestamp alignment mode: TIMESTAMP_TYPE_UNSPECIFIED, WORD, or CHARACTER.
    /// </summary>
    [JsonPropertyName("timestampType")]
    public string? TimestampType { get; set; }

    /// <summary>
    /// Text normalization mode: APPLY_TEXT_NORMALIZATION_UNSPECIFIED, ON, or OFF.
    /// </summary>
    [JsonPropertyName("applyTextNormalization")]
    public string? ApplyTextNormalization { get; set; }
}

public sealed class InworldSpeechAudioConfig
{
    [JsonPropertyName("audioEncoding")]
    public string? AudioEncoding { get; set; }

    [JsonPropertyName("bitRate")]
    public int? BitRate { get; set; }

    [JsonPropertyName("sampleRateHertz")]
    public int? SampleRateHertz { get; set; }

    [JsonPropertyName("speakingRate")]
    public double? SpeakingRate { get; set; }
}
