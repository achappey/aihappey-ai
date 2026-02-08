using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.RegoloAI;

public sealed class RegoloAITranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }
}

