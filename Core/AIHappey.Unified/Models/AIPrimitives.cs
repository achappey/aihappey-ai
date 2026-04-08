namespace AIHappey.Unified.Models;

public sealed class AIInput
{
    public string? Text { get; init; }

    public List<AIInputItem>? Items { get; init; }

    public Dictionary<string, object?>? Metadata { get; init; }
}

public sealed class AIInputItem
{
    public string Type { get; init; } = "message";

    public string? Id { get; init; }

    public string? Role { get; init; }

    public List<AIContentPart>? Content { get; init; }

    public Dictionary<string, object?>? Metadata { get; init; }
}

public sealed class AIOutput
{
    public List<AIOutputItem>? Items { get; init; }

    public Dictionary<string, object?>? Metadata { get; init; }
}

public sealed class AIOutputItem
{
    public string Type { get; init; } = "message";

    public string? Role { get; init; }

    public List<AIContentPart>? Content { get; init; }

    public Dictionary<string, object?>? Metadata { get; init; }
}

public abstract class AIContentPart
{
    public required string Type { get; init; }

    public Dictionary<string, object?>? Metadata { get; init; }
}

public sealed class AITextContentPart : AIContentPart
{
    public AITextContentPart()
    {
        Type = "text";
    }

    public required string Text { get; init; }
}

public sealed class AIFileContentPart : AIContentPart
{
    public AIFileContentPart()
    {
        Type = "file";
    }

    public string? MediaType { get; init; }

    public string? Filename { get; init; }

    public object? Data { get; init; }
}


public sealed class AIToolDefinition
{
    public string Name { get; init; } = default!;

    public string? Title { get; init; }
    
    public string? Description { get; init; }

    public object? InputSchema { get; init; }

    public Dictionary<string, object?>? Metadata { get; init; }
}

public sealed class AIEventEnvelope
{
    public string Type { get; init; } = "event";

    public string? Id { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    public AIInput? Input { get; init; }

    public AIOutput? Output { get; init; }

    public object? Data { get; init; }

    public Dictionary<string, object?>? Metadata { get; init; }

}

