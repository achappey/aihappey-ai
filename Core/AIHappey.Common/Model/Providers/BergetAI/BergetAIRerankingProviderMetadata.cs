using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.BergetAI;

public sealed class BergetAIRerankingProviderMetadata
{
    [JsonPropertyName("return_documents")]
    public bool? ReturnDocuments { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }
}

