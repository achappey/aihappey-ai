namespace AIHappey.Unified.Models;

public sealed class AIStreamEvent
{
    public required string ProviderId { get; init; }

    public required AIEventEnvelope Event { get; init; }

    public Dictionary<string, object?>? Metadata { get; init; }
}

