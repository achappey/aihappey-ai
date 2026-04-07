using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class OpenAiProviderMetadata
{
    [JsonPropertyName("tools")]
    public JsonElement[]? Tools { get; set; }

    public OpenAiReasoning? Reasoning { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("max_tool_calls")]
    public int? MaxToolCalls { get; set; }

    [JsonPropertyName("mcp_list_tools")]
    public IEnumerable<OpenAiMcpTool>? MCPTools { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("truncation")]
    public string? Truncation { get; set; }

    [JsonPropertyName("include")]
    public IEnumerable<string>? Include { get; set; }

    [JsonPropertyName("inputImageDetail")]
    public string? InputImageDetail { get; set; }

    [JsonPropertyName("context_management")]
    public JsonElement[]? ContextManagement { get; set; }

}

