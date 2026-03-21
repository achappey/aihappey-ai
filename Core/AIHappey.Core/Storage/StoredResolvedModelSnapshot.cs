using AIHappey.Core.Models;

namespace AIHappey.Core.Storage;

public sealed class StoredResolvedModelSnapshot
{
    public string AggregateKey { get; set; } = string.Empty;

    public DateTimeOffset StoredAtUtc { get; set; }

    public DateTimeOffset RefreshAfterUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public List<StoredResolvedModelEntry> Entries { get; set; } = [];

    public List<StoredResolvedProviderState> Providers { get; set; } = [];
}

public sealed class StoredResolvedModelEntry
{
    public string ProviderId { get; set; } = string.Empty;

    public Model Model { get; set; } = new();
}

public sealed class StoredResolvedProviderState
{
    public string ProviderId { get; set; } = string.Empty;

    public string CacheKey { get; set; } = string.Empty;

    public string? SourceCacheKey { get; set; }

    public DateTimeOffset StoredAtUtc { get; set; }

    public DateTimeOffset RefreshAfterUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }
}
