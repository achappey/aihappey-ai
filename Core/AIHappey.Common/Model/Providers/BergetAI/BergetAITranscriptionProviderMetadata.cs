using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.BergetAI;

public sealed class BergetAITranscriptionProviderMetadata
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }
}

