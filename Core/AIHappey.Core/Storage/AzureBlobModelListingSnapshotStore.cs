using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using Microsoft.Extensions.Options;

namespace AIHappey.Core.Storage;

public sealed class AzureBlobModelListingSnapshotStore : IModelListingSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly BlobContainerClient _containerClient;

    public AzureBlobModelListingSnapshotStore(IOptions<ModelListingStorageOptions> options)
    {
        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            throw new InvalidOperationException("Model listing storage connection string is required.");

        _containerClient = new BlobContainerClient(settings.ConnectionString, settings.BlobContainerName);
    }

    public Task<StoredProviderModelSnapshot?> GetProviderSnapshotAsync(
        string providerId,
        string cacheKey,
        CancellationToken cancellationToken = default)
        => ReadAsync<StoredProviderModelSnapshot>(GetProviderBlobName(providerId, cacheKey), cancellationToken);

    public Task SaveProviderSnapshotAsync(
        string providerId,
        string cacheKey,
        StoredProviderModelSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => WriteAsync(GetProviderBlobName(providerId, cacheKey), snapshot, cancellationToken);

    public Task<StoredResolvedModelSnapshot?> GetAggregateSnapshotAsync(
        string aggregateKey,
        CancellationToken cancellationToken = default)
        => ReadAsync<StoredResolvedModelSnapshot>(GetAggregateBlobName(aggregateKey), cancellationToken);

    public Task SaveAggregateSnapshotAsync(
        string aggregateKey,
        StoredResolvedModelSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => WriteAsync(GetAggregateBlobName(aggregateKey), snapshot, cancellationToken);

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

    private async Task WriteAsync<T>(string blobName, T value, CancellationToken cancellationToken)
    {
        await _containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var client = _containerClient.GetBlobClient(blobName);
        await client.UploadAsync(BinaryData.FromObjectAsJson(value, JsonOptions), overwrite: true, cancellationToken);
    }

    private static string GetProviderBlobName(string providerId, string cacheKey)
        => $"providers/{providerId}/{Uri.EscapeDataString(cacheKey)}.json";

    private static string GetAggregateBlobName(string aggregateKey)
        => $"aggregate/{Uri.EscapeDataString(aggregateKey)}.json";
}
