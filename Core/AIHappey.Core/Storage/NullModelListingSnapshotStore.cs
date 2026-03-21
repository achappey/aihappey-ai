using AIHappey.Core.Contracts;

namespace AIHappey.Core.Storage;

public sealed class NullModelListingSnapshotStore : IModelListingSnapshotStore
{
    public Task<StoredProviderModelSnapshot?> GetProviderSnapshotAsync(
        string providerId,
        string cacheKey,
        CancellationToken cancellationToken = default)
        => Task.FromResult<StoredProviderModelSnapshot?>(null);

    public Task<StoredProviderModelSnapshot?> GetLatestProviderSnapshotAsync(
        string providerId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<StoredProviderModelSnapshot?>(null);

    public Task SaveProviderSnapshotAsync(
        string providerId,
        string cacheKey,
        StoredProviderModelSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<StoredResolvedModelSnapshot?> GetAggregateSnapshotAsync(
        string aggregateKey,
        CancellationToken cancellationToken = default)
        => Task.FromResult<StoredResolvedModelSnapshot?>(null);

    public Task<StoredResolvedModelSnapshot?> GetLatestAggregateSnapshotAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<StoredResolvedModelSnapshot?>(null);

    public Task SaveAggregateSnapshotAsync(
        string aggregateKey,
        StoredResolvedModelSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
