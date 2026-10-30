using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.XVoice;

public sealed class XVoiceTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }
}
