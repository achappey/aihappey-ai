using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class OpenAiProviderMetadata
{
    public OpenAiReasoning? Reasoning { get; set; }

    [JsonPropertyName("web_search")]
    public OpenAiWebSearch? WebSearch { get; set; }

    [JsonPropertyName("file_search")]
    public OpenAiFileSearch? FileSearch { get; set; }

    [JsonPropertyName("code_interpreter")]
    public CodeInterpreter? CodeInterpreter { get; set; }

    [JsonPropertyName("image_generation")]
    public ImageGeneration? ImageGeneration { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("max_tool_calls")]
    public int? MaxToolCalls { get; set; }

    [JsonPropertyName("mcp_list_tools")]
    public IEnumerable<OpenAiMcpTool>? MCPTools { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("include")]
    public IEnumerable<string>? Include { get; set; }
}

