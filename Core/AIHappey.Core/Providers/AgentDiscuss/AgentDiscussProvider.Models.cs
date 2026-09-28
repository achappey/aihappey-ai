using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.AgentDiscuss;

public partial class AgentDiscussProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];

        return await _memoryCache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var domains = await SendAgentDiscussAsync(HttpMethod.Get, "api/agentic-api/domains", null, ct);
            if (domains.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("AgentDiscuss domain catalog must be an array.");

            var models = new List<Model>();
            foreach (var domain in domains.EnumerateArray())
            {
                if (!string.Equals(GetString(domain, "status"), "live", StringComparison.OrdinalIgnoreCase)) continue;
                var domainKey = GetString(domain, "domainKey");
                if (!IsValidSegment(domainKey)) continue;

                // Catalog paths are metadata, not trusted destinations; construct the URL from the domain key.
                var capabilities = await SendAgentDiscussAsync(HttpMethod.Get,
                    $"api/agentic-api/domains/{Uri.EscapeDataString(domainKey!)}/capabilities", null, ct);
                if (capabilities.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException($"AgentDiscuss capabilities for '{domainKey}' must be an array.");

                foreach (var capability in capabilities.EnumerateArray())
                {
                    if (!string.Equals(GetString(capability, "status"), "live", StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(GetString(capability, "executeEndpoint"))) continue;
                    var capabilityId = GetString(capability, "capabilityId");
                    if (!IsValidSegment(capabilityId)
                        || !string.Equals(GetString(capability, "domainKey"), domainKey, StringComparison.Ordinal)) continue;

                    models.Add(new Model
                    {
                        Id = $"{domainKey}/{capabilityId}".ToModelId(GetIdentifier()),
                        Name = GetString(capability, "label") ?? capabilityId!,
                        Description = GetString(capability, "description"),
                        OwnedBy = "AgentDiscuss",
                        Type = "language",
                        Tags = ["agent", "capability", domainKey!]
                    });
                }
            }

            return (IEnumerable<Model>)models.GroupBy(model => model.Id, StringComparer.Ordinal).Select(group => group.First()).ToList();
        }, baseTtl: TimeSpan.FromMinutes(15), jitterMinutes: 2, cancellationToken: cancellationToken);
    }
}
