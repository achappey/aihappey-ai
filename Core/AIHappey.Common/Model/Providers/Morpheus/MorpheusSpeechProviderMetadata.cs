using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Morpheus;

public sealed class MorpheusSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }
}

