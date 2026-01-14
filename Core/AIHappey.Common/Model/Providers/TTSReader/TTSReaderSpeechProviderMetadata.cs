using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.TTSReader;

public sealed class TTSReaderSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("rate")]
    public float? Rate { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("quality")]
    public string? Quality { get; set; }
}
