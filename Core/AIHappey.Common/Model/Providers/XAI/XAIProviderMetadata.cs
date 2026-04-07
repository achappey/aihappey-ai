using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.XAI;

public sealed class XAIProviderMetadata
{
    [JsonPropertyName("tools")]
    public JsonElement[]? Tools { get; set; }

    [JsonPropertyName("reasoning")]
    public XAIReasoning? Reasoning { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("include")]
    public IEnumerable<string>? Include { get; set; }
}

