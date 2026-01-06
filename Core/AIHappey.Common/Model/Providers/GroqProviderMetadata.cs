using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;



public class GroqTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("timestamp_granularities")]
    public IEnumerable<string>? TimestampGranularities { get; set; }
}


public class GroqSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }
}

public class GroqProviderMetadata
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

public class GroqBrowserSearch
{
    [JsonPropertyName("type")]
    public string? Type { get; set; } = "browser_search";
}


public class GroqReasoning
{
    [JsonPropertyName("effort")]
    public string? Effort { get; set; }    // low, medium, high
}

public class GroqCodeInterpreter
{
    [JsonPropertyName("type")]
    public string? Type { get; set; } = "code_interpreter";

    [JsonPropertyName("container")]
    public GroqCodeInterpreterContainer? Container { get; set; } = new();
}

public class GroqCodeInterpreterContainer
{
    [JsonPropertyName("type")]
    public string? Type { get; set; } = "auto";
}