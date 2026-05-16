using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Assisters;

public sealed class AssistersRerankingProviderMetadata
{
    [JsonPropertyName("return_documents")]
    public bool? ReturnDocuments { get; set; }
}
