using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Groq;

public sealed class GroqProviderMetadata
{
    [JsonPropertyName("reasoning")]
    public GroqReasoning? Reasoning { get; set; }

    [JsonPropertyName("code_interpreter")]
    public GroqCodeInterpreter? CodeInterpreter { get; set; }

    [JsonPropertyName("browser_search")]
    public GroqBrowserSearch? BrowserSearch { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }
}

