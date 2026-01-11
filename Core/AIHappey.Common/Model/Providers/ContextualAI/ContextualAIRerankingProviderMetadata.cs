using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.ContextualAI;

public sealed class ContextualAIRerankingProviderMetadata
{
    [JsonPropertyName("instruction")]
    public string? Instruction { get; set; }

}

