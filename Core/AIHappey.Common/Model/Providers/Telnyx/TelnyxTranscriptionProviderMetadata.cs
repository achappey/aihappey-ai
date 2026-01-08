using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Telnyx;

public sealed class TelnyxTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    /// <summary>
    /// json | verbose_json
    /// </summary>
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    /// <summary>
    /// Only valid when response_format=verbose_json. Telnyx uses OpenAI naming:
    /// timestamp_granularities[]
    /// </summary>
    [JsonPropertyName("timestamp_granularities")]
    public IEnumerable<string>? TimestampGranularities { get; set; }
}

