using System.Runtime.CompilerServices;

namespace AIHappey.HeaderAuth;

/// <summary>
/// Request-scoped snapshot of provider API keys extracted from request headers once.
/// </summary>
public sealed class HeaderApiKeySnapshot(IHttpContextAccessor http)
{
    private readonly ConditionalWeakTable<HttpContext, SnapshotEntry> _cache = new();
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> ProviderKeys
    {
        get
        {
            var ctx = http.HttpContext;
            if (ctx == null)
                return Empty;

            return _cache.GetValue(ctx, static currentCtx =>
                new SnapshotEntry(BuildSnapshot(currentCtx.Request.Headers))).ProviderKeys;
        }
    }

    public string? Resolve(string provider)
        => ProviderKeys.TryGetValue(provider, out var key) ? key : null;

    private static IReadOnlyDictionary<string, string> BuildSnapshot(IHeaderDictionary? headers)
    {
        if (headers == null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (provider, headerName) in HeaderApiKeyResolver.SupportedProviderHeaders)
        {
            var value = headers[headerName].FirstOrDefault()
                ?? headers[headerName.ToLowerInvariant()].FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(value))
                map[provider] = value.Trim();
        }

        return map;
    }

    private sealed record SnapshotEntry(IReadOnlyDictionary<string, string> ProviderKeys);
}
