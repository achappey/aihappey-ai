using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.XAI;

public sealed class XAISpeechProviderMetadata
{
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("output_format")]
    public XAISpeechOutputFormat? OutputFormat { get; set; }
}

public sealed class XAISpeechOutputFormat
{
    [JsonPropertyName("codec")]
    public string? Codec { get; set; }

    [JsonPropertyName("sample_rate")]
    public int? SampleRate { get; set; }

    [JsonPropertyName("bit_rate")]
    public int? BitRate { get; set; }
}
