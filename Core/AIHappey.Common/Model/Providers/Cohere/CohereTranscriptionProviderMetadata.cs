using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Cohere;

public sealed class CohereTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }
}
