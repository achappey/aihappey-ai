using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Verbatik;

/// <summary>
/// Provider options for Verbatik TTS.
/// Consumed via <c>providerOptions.verbatik</c>.
/// </summary>
public sealed class VerbatikSpeechProviderMetadata
{
    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    [JsonPropertyName("volume")]
    public float? Volume { get; set; }

    [JsonPropertyName("pitch")]
    public float? Pitch { get; set; }

    [JsonPropertyName("emotion")]
    public string? Emotion { get; set; }

    [JsonPropertyName("englishNormalization")]
    public bool? EnglishNormalization { get; set; }

    [JsonPropertyName("sampleRate")]
    public int? SampleRate { get; set; }

    [JsonPropertyName("bitrate")]
    public int? Bitrate { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("languageBoost")]
    public string? LanguageBoost { get; set; }

    [JsonPropertyName("voiceModifyPitch")]
    public int? VoiceModifyPitch { get; set; }

    [JsonPropertyName("voiceModifyIntensity")]
    public int? VoiceModifyIntensity { get; set; }

    [JsonPropertyName("voiceModifyTimbre")]
    public int? VoiceModifyTimbre { get; set; }
}

