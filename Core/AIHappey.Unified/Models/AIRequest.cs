namespace AIHappey.Unified.Models;

public sealed class AIRequest
{
    public required string ProviderId { get; init; }

    public string? Model { get; init; }
    
    public string? Id { get; init; }

    public string? Instructions { get; init; }

    public AIInput? Input { get; init; }

    public float? Temperature { get; init; }

    public double? TopP { get; init; }

    public int? MaxOutputTokens { get; init; }

    public int? MaxToolCalls { get; init; }

    public bool? Stream { get; init; }

    public bool? ParallelToolCalls { get; init; }

    public object? ToolChoice { get; init; }

    public object? ResponseFormat { get; init; }

    public List<AIToolDefinition>? Tools { get; init; }

    public Dictionary<string, object?>? Metadata { get; init; }

    public Dictionary<string, string>? Headers { get; init; }
}


