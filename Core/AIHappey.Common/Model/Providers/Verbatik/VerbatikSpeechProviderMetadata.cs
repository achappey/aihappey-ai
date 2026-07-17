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

    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

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

    // ---- Verbatik Text-to-Music (POST /api/v1/text-to-music) ----

    [JsonPropertyName("tags")]
    public string[]? Tags { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("prompt_strength")]
    public double? PromptStrength { get; set; }

    [JsonPropertyName("promptStrength")]
    public double? PromptStrengthCamel { get; set; }

    [JsonPropertyName("balance_strength")]
    public double? BalanceStrength { get; set; }

    [JsonPropertyName("balanceStrength")]
    public double? BalanceStrengthCamel { get; set; }

    [JsonPropertyName("num_songs")]
    public int? NumSongs { get; set; }

    [JsonPropertyName("numSongs")]
    public int? NumSongsCamel { get; set; }

    [JsonPropertyName("output_bit_rate")]
    public string? OutputBitRate { get; set; }

    [JsonPropertyName("outputBitRate")]
    public string? OutputBitRateCamel { get; set; }

    [JsonPropertyName("bpm")]
    public object? Bpm { get; set; }

    [JsonPropertyName("store_audio")]
    public bool? StoreAudio { get; set; }

    [JsonPropertyName("storeAudio")]
    public bool? StoreAudioCamel { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

