using System.Text.Json.Serialization;

namespace AIHappey.Core.Models;

public class InputContentDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = null!; // "input_text", "input_image", etc.

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }
}

public class InputMessageDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = null!; // "user" or "assistant"

    [JsonPropertyName("content")]
    public List<InputContentDto> Content { get; set; } = [];
}

public class RequestDto
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    [JsonPropertyName("input")]
    public List<InputMessageDto> Input { get; set; } = [];

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

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
    public List<ToolDto>? Tools { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("truncation")]
    public string? Truncation { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("metadata")]
    public object? Metadata { get; set; }
}

public class ReasoningDto
{
    [JsonPropertyName("effort")]
    public string? Effort { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }
}


public class ToolDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("functionParameters")]
    public object? FunctionParameters { get; set; }
}

public class TextFormatDto
{
    [JsonPropertyName("format")]
    public FormatTypeDto? Format { get; set; }
}

public class FormatTypeDto
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

