namespace AIHappey.Core.Models;

public class ModelListingOptions
{
    /// <summary>
    /// Provider identifiers used for discovery when no explicit provider keys are configured.
    /// </summary>
    public string[] AlwaysIncludeProviders { get; set; } = [];

    public TimeSpan MemoryCacheTtl { get; set; } = TimeSpan.FromMinutes(20);

    public int MaxParallelFirstLoad { get; set; } = 8;
}
