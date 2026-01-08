using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Novita;

public sealed class NovitaMiniMaxSpeechProviderMetadata
{
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }       // Emily/James/Olivia/Michael/Sarah/John

    [JsonPropertyName("language_boost")]
    public string? LanguageBoost { get; set; } // en-US/zh-CN/ja-JP

    [JsonPropertyName("format")]
    public string? Format { get; set; }        // wav/mp3

    [JsonPropertyName("vol")]
    public double? Vol { get; set; }           // 1.0..2.0

    [JsonPropertyName("speed")]
    public double? Speed { get; set; }         // 0.8..3.0

    [JsonPropertyName("emotion")]
    public string? Emotion { get; set; }

    [JsonPropertyName("pitch")]
    public int? Pitch { get; set; }
}

