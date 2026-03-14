using AIHappey.Core.Models;

namespace AIHappey.Core.Storage;

public sealed class StoredProviderModelSnapshot
{
    public string ProviderId { get; set; } = string.Empty;

    public string CacheKey { get; set; } = string.Empty;

    public DateTimeOffset StoredAtUtc { get; set; }

    public DateTimeOffset RefreshAfterUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public List<Model> Models { get; set; } = [];
}
