using AIHappey.Core.Storage;

namespace AIHappey.Core.Contracts;

public interface IModelListingSnapshotStore
{
    Task<StoredProviderModelSnapshot?> GetProviderSnapshotAsync(
        string providerId,
        string cacheKey,
        CancellationToken cancellationToken = default);

    Task SaveProviderSnapshotAsync(
        string providerId,
        string cacheKey,
        StoredProviderModelSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task<StoredResolvedModelSnapshot?> GetAggregateSnapshotAsync(
        string aggregateKey,
        CancellationToken cancellationToken = default);

    Task SaveAggregateSnapshotAsync(
        string aggregateKey,
        StoredResolvedModelSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
