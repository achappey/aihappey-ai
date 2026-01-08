using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.ElevenLabs;

public sealed class ElevenLabsSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    /// <summary>
    /// Query-string <c>output_format</c> e.g. <c>mp3_44100_128</c>.
    /// </summary>
    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("enable_logging")]
    public bool? EnableLogging { get; set; }

    [JsonPropertyName("optimize_streaming_latency")]
    public int? OptimizeStreamingLatency { get; set; }

    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; set; }

    [JsonPropertyName("seed")]
    public uint? Seed { get; set; }

    [JsonPropertyName("voice_settings")]
    public ElevenLabsVoiceSettings? VoiceSettings { get; set; }
}

