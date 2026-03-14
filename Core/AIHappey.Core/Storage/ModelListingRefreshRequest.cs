namespace AIHappey.Core.Storage;

public sealed class ModelListingRefreshRequest
{
    public required string ProviderId { get; init; }

    public required string CacheKey { get; init; }

    public DateTimeOffset RequestedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
