using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public sealed class GoogleSpeechProviderMetadata
{
    [JsonPropertyName("ttsModel")]
    public string? TtsModel { get; set; }

    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    /// <summary>
    /// Multi-speaker configuration.
    /// If present, the provider will build a MultiSpeakerVoiceConfig.
    /// </summary>
    [JsonPropertyName("speakers")]
    public List<GoogleSpeechSpeaker>? Speakers { get; set; }
}

public sealed class GoogleSpeechSpeaker
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("voice")]
    public string? Voice { get; set; }
}

