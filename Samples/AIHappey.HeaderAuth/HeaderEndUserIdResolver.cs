using AIHappey.Common.Model;
using AIHappey.Core.Contracts;
using AIHappey.Core.Orchestration;

namespace AIHappey.HeaderAuth;

/// <summary>
/// HeaderAuth strategy: one-way hash of all present provider API keys from supported headers.
/// Canonical input format: sorted provider=value pairs joined with '|'.
/// </summary>
public sealed class HeaderEndUserIdResolver(
    HeaderApiKeySnapshot snapshot,
    EndUserIdHasher hasher) : IEndUserIdResolver
{
    public string? Resolve(ChatRequest chatRequest)
    {
        var entries = snapshot.ProviderKeys
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value.Trim(), StringComparer.Ordinal);

        if (entries.Count == 0)
            return null;

        var canonical = string.Join("|", entries.Select(kv => $"{kv.Key}={kv.Value}"));
        return hasher.Hash(canonical);
    }
}

