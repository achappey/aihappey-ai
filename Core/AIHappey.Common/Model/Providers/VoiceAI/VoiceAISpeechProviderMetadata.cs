using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.VoiceAI;

/// <summary>
/// Provider-specific options for VoiceAI text-to-speech.
/// Consumed via <c>providerOptions.voiceai</c>.
/// </summary>
public sealed class VoiceAISpeechProviderMetadata
{
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("audio_format")]
    public string? AudioFormat { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }
}
