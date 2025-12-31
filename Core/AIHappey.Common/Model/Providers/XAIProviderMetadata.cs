
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public class XAIImageProviderMetadata
{
}

public class XAIProviderMetadata
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

public class XAIReasoning
{
}

public class XAIWebSearch
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "web_search";

    [JsonPropertyName("allowed_domains")]
    public List<string>? AllowedDomains { get; set; }

    [JsonPropertyName("excluded_domains")]
    public List<string>? ExcludedDomains { get; set; }

    [JsonPropertyName("enable_image_understanding")]
    public bool? EnableImageUnderstanding { get; set; }
}

public class XAIXSearch
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "x_search";

    [JsonPropertyName("allowed_x_handles")]
    public List<string>? AllowedXHandles { get; set; }

    [JsonPropertyName("excluded_x_handles")]
    public List<string>? ExcludedXHandles { get; set; }

    [JsonPropertyName("enable_image_understanding")]
    public bool? EnableImageUnderstanding { get; set; }

    [JsonPropertyName("enable_video_understanding")]
    public bool? EnableVideoUnderstanding { get; set; }

    [JsonPropertyName("from_date")]
    public DateTime? FromDate { get; set; }

    [JsonPropertyName("to_date")]
    public DateTime? ToDate { get; set; }
}

public class XAIXCodeExecution
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "code_interpreter";
}