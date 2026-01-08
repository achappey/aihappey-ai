using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.SambaNova;

public class SambaNovaTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }
}

