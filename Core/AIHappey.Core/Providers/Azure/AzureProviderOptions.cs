namespace AIHappey.Core.Providers.Azure;

public sealed class AzureProviderOptions
{
    public string? Endpoint { get; set; }

    public AzureSkillsStorageOptions? SkillsStorage { get; set; } = new();
}


public sealed class AzureSkillsStorageOptions
{
    public string? ConnectionString { get; set; }

    public string BlobContainerName { get; set; } = string.Empty;
}
