using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Google;

public sealed class GoogleSpeechProviderMetadata
{
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

