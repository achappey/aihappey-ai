
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;



public class SambaNovaTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }
}


public class SambaNovaProviderMetadata
{
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("chat_template_kwargs")]
    public Dictionary<string, object>? ChatTemplateKwargs { get; set; }

}
