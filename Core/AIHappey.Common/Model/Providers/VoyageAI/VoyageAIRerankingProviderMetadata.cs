using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.VoyageAI;

public sealed class VoyageAIRerankingProviderMetadata
{
    [JsonPropertyName("return_documents")]
    public bool? ReturnDocuments { get; set; }

    [JsonPropertyName("truncation")]
    public bool? Truncation { get; set; }
}

