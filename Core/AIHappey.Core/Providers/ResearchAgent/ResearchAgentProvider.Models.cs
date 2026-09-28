using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.ResearchAgent;

public partial class ResearchAgentProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];

        return await _memoryCache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var catalog = await SendAsync(HttpMethod.Get, "agents", null, null, ct);
            if (catalog.ValueKind != JsonValueKind.Object || !catalog.TryGetProperty("agents", out var agents)
                || agents.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("ResearchAgent agent catalog must contain an agents array.");

            var models = new List<Model>
            {
                new()
                {
                    Id = "research".ToModelId(GetIdentifier()), Name = "Research",
                    Description = "ResearchAgent live research; send a prompt or raw JSON request body.",
                    OwnedBy = "ResearchAgent", Type = "language", Tags = ["agent", "research"]
                }
            };
            foreach (var agent in agents.EnumerateArray())
            {
                if (agent.ValueKind != JsonValueKind.Object) continue;
                var id = GetString(agent, "id");
                if (!IsValidId(id) || agent.TryGetProperty("is_active", out var active)
                    && active.ValueKind == JsonValueKind.False) continue;
                models.Add(new Model
                {
                    Id = $"agents/{id}".ToModelId(GetIdentifier()),
                    Name = GetString(agent, "name") ?? id!,
                    Description = GetString(agent, "description"),
                    OwnedBy = "ResearchAgent", Type = "language", Tags = ["agent"]
                });
            }
            return (IEnumerable<Model>)models.GroupBy(model => model.Id, StringComparer.Ordinal)
                .Select(group => group.First()).ToList();
        }, baseTtl: TimeSpan.FromMinutes(15), jitterMinutes: 2, cancellationToken: cancellationToken);
    }
}
