using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Mistral;

public sealed class MistralSpeechProviderMetadata
{
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }
}
