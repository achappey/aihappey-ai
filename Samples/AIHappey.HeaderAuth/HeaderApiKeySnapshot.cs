using System.Runtime.CompilerServices;

namespace AIHappey.HeaderAuth;

/// <summary>
/// Request-scoped snapshot of provider API keys extracted from request headers once.
/// </summary>
public sealed class HeaderApiKeySnapshot(IHttpContextAccessor http)
{
    private readonly ConditionalWeakTable<HttpContext, SnapshotEntry> _cache = new();
    private static readonly IReadOnlyDictionary<string, string> EmptyProviderKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> ProviderKeys => GetSnapshot().ProviderKeys;

    public string? Resolve(string provider)
    {
        var snapshot = GetSnapshot();

        if (!string.IsNullOrWhiteSpace(snapshot.BearerToken)
            && string.Equals(snapshot.ActiveProvider, provider, StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.BearerToken;
        }

        return snapshot.ProviderKeys.TryGetValue(provider, out var key) ? key : null;
    }

    private SnapshotEntry GetSnapshot()
    {
        var ctx = http.HttpContext;
        if (ctx == null)
            return SnapshotEntry.Empty;

        return _cache.GetValue(ctx, static currentCtx => BuildSnapshot(currentCtx));
    }

    private static SnapshotEntry BuildSnapshot(HttpContext context)
    {
        var headers = context.Request.Headers;

        if (headers == null)
            return SnapshotEntry.Empty;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (provider, headerName) in HeaderApiKeyResolver.SupportedProviderHeaders)
        {
            var value = headers[headerName].FirstOrDefault()
                ?? headers[headerName.ToLowerInvariant()].FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(value))
                map[provider] = value.Trim();
        }

        return new SnapshotEntry(map, ResolveBearerToken(headers), HeaderAuthModelContext.TryGetActiveProvider(context));
    }

    private static string? ResolveBearerToken(IHeaderDictionary headers)
    {
        var authorization = headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorization))
            return null;

        const string bearerPrefix = "Bearer ";
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = authorization[bearerPrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private sealed record SnapshotEntry(IReadOnlyDictionary<string, string> ProviderKeys, string? BearerToken, string? ActiveProvider)
    {
        public static SnapshotEntry Empty { get; } = new(EmptyProviderKeys, null, null);
    }
}
