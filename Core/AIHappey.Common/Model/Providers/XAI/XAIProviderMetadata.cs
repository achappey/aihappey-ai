using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.XAI;

public sealed class XAIProviderMetadata
{
    [JsonPropertyName("web_search")]
    public XAIWebSearch? WebSearch { get; set; }

    [JsonPropertyName("reasoning")]
    public XAIReasoning? Reasoning { get; set; }

    [JsonPropertyName("x_search")]
    public XAIXSearch? XSearch { get; set; }

    [JsonPropertyName("code_execution")]
    public XAIXCodeExecution? CodeExecution { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }
}

