using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.Dust;

public partial class DustProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        // Dust public keys are workspace-scoped; the public API does not expose a workspace-list route.
        var workspace = _httpContextAccessor?.HttpContext?.Request.Headers["X-Dust-Workspace-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(workspace)) return [];
        Segment(workspace);
        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key) + ":" + workspace,
            async ct =>
            {
                var root = await SendJsonAsync(HttpMethod.Get,
                    $"w/{Segment(workspace)}/assistant/agent_configurations?view=all", null, ct);
                if (root["agentConfigurations"] is not JsonArray agents)
                    throw new InvalidOperationException("Dust did not return agentConfigurations.");
                return (IEnumerable<Model>)agents.OfType<JsonObject>()
                    .Where(agent => Str(agent, "sId") is not null && Str(agent, "status") is null or "active")
                    .Select(agent =>
                    {
                        var id = Str(agent, "sId")!;
                        Segment(id);
                        return new Model
                        {
                            Id = $"dust/{workspace}/{id}", Name = Str(agent, "name") ?? id,
                            Description = Str(agent, "description"), OwnedBy = "Dust", Type = "language",
                            Tags = ["agent", "conversation", "tools"]
                        };
                    }).ToArray();
            },
            baseTtl: TimeSpan.FromMinutes(15), jitterMinutes: 2, cancellationToken: cancellationToken);
    }
}
