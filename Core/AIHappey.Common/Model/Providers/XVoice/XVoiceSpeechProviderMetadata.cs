using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.XVoice;

public sealed class XVoiceSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("speed")]
    public double? Speed { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("stream_format")]
    public string? StreamFormat { get; set; }
}
