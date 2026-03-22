using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using Microsoft.Extensions.Options;
using Azure.Storage.Blobs.Models;

namespace AIHappey.Core.Storage;

public sealed class AzureBlobModelListingSnapshotStore : IModelListingSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly BlobContainerClient _containerClient;
    private readonly bool _includeApiKeysInSnapshotIdentity;

    public AzureBlobModelListingSnapshotStore(IOptions<ModelListingStorageOptions> options)
    {
        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            throw new InvalidOperationException("Model listing storage connection string is required.");

        _containerClient = new BlobContainerClient(settings.ConnectionString, settings.BlobContainerName);
        _includeApiKeysInSnapshotIdentity = settings.IncludeApiKeysInSnapshotIdentity;
    }

    public Task<StoredProviderModelSnapshot?> GetProviderSnapshotAsync(
        string providerId,
        string cacheKey,
        CancellationToken cancellationToken = default)
        => ReadAsync<StoredProviderModelSnapshot>(GetProviderBlobName(providerId, cacheKey), cancellationToken);

    public Task<StoredProviderModelSnapshot?> GetLatestProviderSnapshotAsync(
        string providerId,
        CancellationToken cancellationToken = default)
        => GetLatestProviderSnapshotCoreAsync(providerId, cancellationToken);

    public Task SaveProviderSnapshotAsync(
        string providerId,
        string cacheKey,
        StoredProviderModelSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => SaveProviderSnapshotCoreAsync(providerId, cacheKey, snapshot, cancellationToken);

    public Task<StoredResolvedModelSnapshot?> GetAggregateSnapshotAsync(
        string aggregateKey,
        CancellationToken cancellationToken = default)
        => ReadAsync<StoredResolvedModelSnapshot>(GetAggregateBlobName(aggregateKey), cancellationToken);

    public Task<StoredResolvedModelSnapshot?> GetLatestAggregateSnapshotAsync(
        CancellationToken cancellationToken = default)
        => GetLatestAggregateSnapshotCoreAsync(cancellationToken);

    public Task SaveAggregateSnapshotAsync(
        string aggregateKey,
        StoredResolvedModelSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => SaveAggregateSnapshotCoreAsync(aggregateKey, snapshot, cancellationToken);

    private async Task<StoredProviderModelSnapshot?> GetLatestProviderSnapshotCoreAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        if (!_includeApiKeysInSnapshotIdentity)
        {
            var latestAlias = await TryReadIfExistsAsync<StoredProviderModelSnapshot>(GetLatestProviderBlobName(providerId), cancellationToken);
            if (latestAlias?.Models.Count > 0)
                return latestAlias;

            return null;
        }

        return await GetLatestSnapshotAsync<StoredProviderModelSnapshot>(
            $"providers/{providerId}/",
            snapshot => snapshot.Models.Count > 0,
            cancellationToken);
    }

    private async Task<StoredResolvedModelSnapshot?> GetLatestAggregateSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        if (!_includeApiKeysInSnapshotIdentity)
        {
            var latestAlias = await TryReadIfExistsAsync<StoredResolvedModelSnapshot>(GetLatestAggregateBlobName(), cancellationToken);
            if (latestAlias?.Entries.Count > 0)
                return latestAlias;

            return null;
        }

        return await GetLatestSnapshotAsync<StoredResolvedModelSnapshot>(
            "aggregate/",
            snapshot => snapshot.Entries.Count > 0,
            cancellationToken);
    }

    private async Task SaveAggregateSnapshotCoreAsync(
        string aggregateKey,
        StoredResolvedModelSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await WriteAsync(GetAggregateBlobName(aggregateKey), snapshot, cancellationToken);

        if (!_includeApiKeysInSnapshotIdentity)
            await WriteAsync(GetLatestAggregateBlobName(), snapshot, cancellationToken);
    }

    private async Task<T?> ReadAsync<T>(string blobName, CancellationToken cancellationToken)
    {
        try
        {
            await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            var client = _containerClient.GetBlobClient(blobName);
            var response = await client.DownloadContentAsync(cancellationToken);
            return response.Value.Content.ToObjectFromJson<T>(JsonOptions);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return default;
        }
    }

    private async Task<T?> TryReadIfExistsAsync<T>(string blobName, CancellationToken cancellationToken)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var client = _containerClient.GetBlobClient(blobName);
        var exists = await client.ExistsAsync(cancellationToken);
        if (!exists.Value)
            return default;

        var response = await client.DownloadContentAsync(cancellationToken);
        return response.Value.Content.ToObjectFromJson<T>(JsonOptions);
    }

    private async Task WriteAsync<T>(string blobName, T value, CancellationToken cancellationToken)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var client = _containerClient.GetBlobClient(blobName);
        await client.UploadAsync(BinaryData.FromObjectAsJson(value, JsonOptions), overwrite: true, cancellationToken);
    }

    private async Task SaveProviderSnapshotCoreAsync(
        string providerId,
        string cacheKey,
        StoredProviderModelSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await WriteAsync(GetProviderBlobName(providerId, cacheKey), snapshot, cancellationToken);

        if (!_includeApiKeysInSnapshotIdentity)
            await WriteAsync(GetLatestProviderBlobName(providerId), snapshot, cancellationToken);
    }

    private async Task<T?> GetLatestSnapshotAsync<T>(
        string prefix,
        Func<T, bool> isUsable,
        CancellationToken cancellationToken)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var candidates = new List<(string Name, DateTimeOffset LastModifiedUtc)>();

        await foreach (var blobItem in _containerClient.GetBlobsAsync(
                traits: BlobTraits.None,
                states: BlobStates.None,
                prefix: prefix, cancellationToken: cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(blobItem.Name))
                continue;

            candidates.Add((blobItem.Name, blobItem.Properties.LastModified ?? DateTimeOffset.MinValue));
        }

        foreach (var candidate in candidates
                     .OrderByDescending(item => item.LastModifiedUtc)
                     .Take(1))
        {
            var snapshot = await ReadAsync<T>(candidate.Name, cancellationToken);
            if (snapshot == null || !isUsable(snapshot))
                continue;

            return snapshot;
        }

        return default;
    }

    private static string GetProviderBlobName(string providerId, string cacheKey)
        => $"providers/{providerId}/{Uri.EscapeDataString(cacheKey)}.json";

    private static string GetLatestProviderBlobName(string providerId)
        => $"providers/{providerId}/latest.json";

    private static string GetAggregateBlobName(string aggregateKey)
        => $"aggregate/{Uri.EscapeDataString(aggregateKey)}.json";

    private static string GetLatestAggregateBlobName()
        => "aggregate/latest.json";
}
