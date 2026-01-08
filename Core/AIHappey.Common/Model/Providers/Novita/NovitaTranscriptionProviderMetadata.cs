using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Novita;

public class NovitaTranscriptionProviderMetadata
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("hotwords")]
    public IEnumerable<string>? Hotwords { get; set; }
}

