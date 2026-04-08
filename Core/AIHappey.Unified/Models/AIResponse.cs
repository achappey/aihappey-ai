
namespace AIHappey.Unified.Models;

public sealed class AIResponse
{
    public required string ProviderId { get; init; }

    public string? Model { get; init; }

    public string? Status { get; init; }

    public AIOutput? Output { get; init; }

    public object? Usage { get; init; }

    public Dictionary<string, object?>? Metadata { get; init; }

   // public List<AIEventEnvelope>? Events { get; init; }
}