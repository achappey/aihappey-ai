using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Novita;

public sealed class NovitaText2SpeechSpeechProviderMetadata
{
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }  // Emily/James/Olivia/Michael/Sarah/John

    [JsonPropertyName("language")]
    public string? Language { get; set; } // en-US/zh-CN/ja-JP

    [JsonPropertyName("volume")]
    public double? Volume { get; set; }   // 1.0..2.0

    [JsonPropertyName("speed")]
    public double? Speed { get; set; }    // 0.8..3.0
}

