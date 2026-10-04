using System.Collections.Concurrent;

namespace AIHappey.Core.Orchestration;

// Shared coordination contains identifiers only, never providers or request scopes.
public sealed class ModelListingRefreshState
{
    internal ConcurrentDictionary<string, byte> AggregateRefreshes { get; } = new(StringComparer.Ordinal);
    internal ConcurrentDictionary<string, byte> QueuedProviders { get; } = new(StringComparer.OrdinalIgnoreCase);
}
