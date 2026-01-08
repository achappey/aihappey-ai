using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Zai;

public class ZaiTranscriptionProviderMetadata
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("hotwords")]
    public IEnumerable<string>? Hotwords { get; set; }
}

