using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Groq;

public sealed class GroqSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }
}

