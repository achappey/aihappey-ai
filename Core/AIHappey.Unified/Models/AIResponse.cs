
namespace AIHappey.Unified.Models;

public sealed class AIResponse
{
    public required string ProviderId { get; init; }

    public string? Model { get; init; }

    public string? Status { get; init; }

    public AIOutput? Output { get; init; }

    /// <summary>
    /// Usage remains object-typed for source compatibility. Unified protocol
    /// mappers populate it with <see cref="AIUsage"/>.
    /// </summary>
    public object? Usage { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public AIUsage? NormalizedUsage => Usage as AIUsage;

    public Dictionary<string, object?>? Metadata { get; init; }

   // public List<AIEventEnvelope>? Events { get; init; }
}
