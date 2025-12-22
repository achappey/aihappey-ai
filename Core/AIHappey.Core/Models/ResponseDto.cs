using System.Text.Json.Serialization;

namespace AIHappey.Core.Models;

public class ResponseDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = null!;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "response";

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = null!;

    [JsonPropertyName("error")]
    public object? Error { get; set; }

    [JsonPropertyName("incomplete_details")]
    public object? IncompleteDetails { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    [JsonPropertyName("output")]
    public List<OutputItemDto> Output { get; set; } = [];

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; set; }

    [JsonPropertyName("reasoning")]
    public ReasoningDto? Reasoning { get; set; }

    [JsonPropertyName("store")]
    public bool? Store { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("text")]
    public TextFormatDto? Text { get; set; }

    [JsonPropertyName("tool_choice")]
    public string? ToolChoice { get; set; }

    [JsonPropertyName("tools")]
    public List<object>? Tools { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("truncation")]
    public string? Truncation { get; set; }

    [JsonPropertyName("usage")]
    public UsageDto? Usage { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("metadata")]
    public object? Metadata { get; set; }
}

public class OutputItemDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "message";

    [JsonPropertyName("id")]
    public string Id { get; set; } = null!;

    [JsonPropertyName("status")]
    public string Status { get; set; } = null!;

    [JsonPropertyName("role")]
    public string Role { get; set; } = "assistant";

    [JsonPropertyName("content")]
    public List<OutputContentDto> Content { get; set; } = [];
}

public class OutputContentDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "output_text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

    [JsonPropertyName("annotations")]
    public List<object> Annotations { get; set; } = [];
}

public class UsageDto
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("input_tokens_details")]
    public InputTokensDetailsDto? InputTokensDetails { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("output_tokens_details")]
    public OutputTokensDetailsDto? OutputTokensDetails { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

public class InputTokensDetailsDto
{
    [JsonPropertyName("cached_tokens")]
    public int CachedTokens { get; set; }
}

public class OutputTokensDetailsDto
{
    [JsonPropertyName("reasoning_tokens")]
    public int ReasoningTokens { get; set; }
}

// ReasoningDto and TextFormatDto are reused from RequestDto.cs

