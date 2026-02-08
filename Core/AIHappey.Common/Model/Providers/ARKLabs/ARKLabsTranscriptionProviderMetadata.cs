using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.ARKLabs;

public sealed class ARKLabsTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    /// <summary>
    /// json | srt
    /// </summary>
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }
}

