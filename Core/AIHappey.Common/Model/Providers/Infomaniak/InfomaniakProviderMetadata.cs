using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Infomaniak;

public class InfomaniakProviderMetadata
{
    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("presence_penalty")]
    public float? PresencePenalty { get; set; }

    [JsonPropertyName("stop")]
    public IEnumerable<string>? Stop { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("n")]
    public int? N { get; set; }

    [JsonPropertyName("logprobs")]
    public bool? LogProbs { get; set; }

    [JsonPropertyName("top_logprobs")]
    public int? TopLogProbs { get; set; }

    [JsonPropertyName("logit_bias")]
    public Dictionary<string, int>? LogitBias { get; set; }

    [JsonPropertyName("stream_options_include_usage")]
    public bool? StreamOptionsIncludeUsage { get; set; }

    [JsonPropertyName("stream_options_include_obfuscation")]
    public bool? StreamOptionsIncludeObfuscation { get; set; }
}

