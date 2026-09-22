using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.AgentContainer;

public partial class AgentContainerProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                var agents = await ListAllAgentContainerAgentsAsync(ct);
                var models = new List<Model>(agents.Count * 2);

                foreach (var agent in agents)
                {
                    if (GetAgentContainerString(agent, "archivedAt") is not null)
                        continue;

                    var id = GetAgentContainerString(agent, "id");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var name = GetAgentContainerString(agent, "name") ?? id;
                    var description = GetAgentContainerString(agent, "description");
                    var created = GetAgentContainerDate(agent, "createdAt")?.ToUnixTimeSeconds();

                    models.Add(new Model
                    {
                        Id = $"{id}/conversation".ToModelId(GetIdentifier()),
                        Name = $"{name} · Conversation",
                        Description = string.IsNullOrWhiteSpace(description)
                            ? $"AgentContainer conversational session for '{name}'."
                            : $"{description} (conversation)",
                        OwnedBy = nameof(AgentContainer),
                        Type = "language",
                        Created = created,
                        Tags = ["agent", "conversation", "stateful", "tools"]
                    });

                    models.Add(new Model
                    {
                        Id = $"{id}/task".ToModelId(GetIdentifier()),
                        Name = $"{name} · Task",
                        Description = string.IsNullOrWhiteSpace(description)
                            ? $"AgentContainer autonomous task for '{name}'."
                            : $"{description} (autonomous task)",
                        OwnedBy = nameof(AgentContainer),
                        Type = "language",
                        Created = created,
                        Tags = ["agent", "task", "autonomous", "stateful", "tools"]
                    });
                }

                return (IEnumerable<Model>)models;
            },
            baseTtl: TimeSpan.FromMinutes(15),
            jitterMinutes: 2,
            cancellationToken: cancellationToken);
    }

    private async Task<List<JsonElement>> ListAllAgentContainerAgentsAsync(CancellationToken cancellationToken)
    {
        var agents = new List<JsonElement>();
        string? cursor = null;

        do
        {
            var path = "agents?limit=100";
            if (!string.IsNullOrWhiteSpace(cursor))
                path += $"&cursor={Uri.EscapeDataString(cursor)}";

            var root = await SendAgentContainerJsonAsync(
                HttpMethod.Get,
                path,
                payload: null,
                operation: "list agents",
                idempotencyKey: null,
                cancellationToken);

            if (!TryGetAgentContainerProperty(root, "data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                break;

            agents.AddRange(data.EnumerateArray().Select(item => item.Clone()));
            cursor = TryGetAgentContainerProperty(root, "pageInfo", out var pageInfo)
                && GetAgentContainerBoolean(pageInfo, "hasMore") == true
                    ? GetAgentContainerString(pageInfo, "nextCursor")
                    : null;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return agents;
    }
}
