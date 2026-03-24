namespace AIHappey.Core.Models;

public class ModelListingStorageOptions
{
    public string? ConnectionString { get; set; }

    public bool DisableModelDiscovery { get; set; } = false;

    public string BlobContainerName { get; set; } = "model-listings";

    public string? QueueName { get; set; }

    public bool IncludeApiKeysInSnapshotIdentity { get; set; } = true;

    /// <summary>
    /// Provider identifiers that should always be included in discovery attempts,
    /// even when keyed-first header-auth filtering is active.
    /// </summary>
    public string[] AlwaysIncludeProviders { get; set; } = [];

    public TimeSpan ProviderRefreshAfter { get; set; } = TimeSpan.FromHours(8);

    public int ProviderRefreshJitterMinutes { get; set; } = 480;

    public TimeSpan ProviderSnapshotTtl { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan AggregateRefreshAfter { get; set; } = TimeSpan.FromMinutes(30);

    public int AggregateRefreshJitterMinutes { get; set; } = 30;

    public TimeSpan AggregateSnapshotTtl { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan MemoryCacheTtl { get; set; } = TimeSpan.FromMinutes(20);

    public int MaxParallelFirstLoad { get; set; } = 8;

    public int MaxParallelBackgroundRefresh { get; set; } = 4;
}
