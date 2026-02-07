using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.GreenPT;

public sealed class GreenPTRerankingProviderMetadata
{
    [JsonPropertyName("return_documents")]
    public bool? ReturnDocuments { get; set; }
}

