using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Novita;

public sealed class NovitaGlmSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }          // Emily/James/Olivia/Michael/Sarah/John

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; } // wav/mp3

    [JsonPropertyName("volume")]
    public double? Volume { get; set; }         // 1.0..2.0

    [JsonPropertyName("speed")]
    public double? Speed { get; set; }          // 0.8..3.0

    [JsonPropertyName("watermark_enabled")]
    public bool? WatermarkEnabled { get; set; }
}

