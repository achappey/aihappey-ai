using AIHappey.Common.Model;
using AIHappey.Core.Contracts;
using AIHappey.Core.Orchestration;

namespace AIHappey.HeaderAuth;

/// <summary>
/// HeaderAuth strategy: one-way hash of all present provider API keys from supported headers.
/// Canonical input format: sorted provider=value pairs joined with '|'.
/// </summary>
public sealed class HeaderEndUserIdResolver(
    IHttpContextAccessor http,
    EndUserIdHasher hasher) : IEndUserIdResolver
{
    public string? Resolve(ChatRequest chatRequest)
    {
        var ctx = http.HttpContext;
        if (ctx == null)
            return null;

        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var headers = ctx.Request.Headers;

        foreach (var kv in HeaderApiKeyResolver.SupportedProviderHeaders)
        {
            var headerName = kv.Value;
            var value = headers[headerName].FirstOrDefault()
                ?? headers[headerName.ToLowerInvariant()].FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(value))
                entries[kv.Key.ToLowerInvariant()] = value.Trim();
        }

        if (entries.Count == 0)
            return null;

        var canonical = string.Join("|", entries.Select(kv => $"{kv.Key}={kv.Value}"));
        return hasher.Hash(canonical);
    }
}

