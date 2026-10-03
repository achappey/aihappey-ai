using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Skyvern;

public partial class SkyvernProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier()))) return [];
        var transport = GetTransport();
        return await _memoryCache.GetOrCreateAsync(this.GetCacheKey(transport.Key), async ct =>
        {
            const int pageSize = 100;
            var models = new Dictionary<string, Model>(StringComparer.Ordinal);
            for (var page = 1; ; page++)
            {
                var reply = await SendJson(transport, HttpMethod.Get, $"v1/agents?page={page}&page_size={pageSize}", null, ct);
                if (reply.Raw.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Skyvern agent catalog must be an array.");
                var agents = reply.Raw.EnumerateArray().ToList();
                var newIds = 0;
                foreach (var agent in agents)
                {
                    var id = String(agent, "agent_id") ?? String(agent, "workflow_permanent_id");
                    if (id is null || Property(agent, "deleted_at") is { ValueKind: JsonValueKind.String }) continue;
                    var name = String(agent, "title") ?? id;
                    var created = String(agent, "original_created_at") ?? String(agent, "created_at");
                    var definition = Property(agent, "workflow_definition") ?? default;
                    var parameters = Array(definition, "parameters")
                        .Where(p => String(p, "parameter_type") == "workflow" && Property(p, "deleted_at") is not { ValueKind: JsonValueKind.String })
                        .Select(p => $"{String(p, "key")} ({String(p, "workflow_parameter_type")}): {String(p, "description")}");
                    var description = $"{String(agent, "description")}\nInput: strict JSON object of workflow parameters. {string.Join("; ", parameters)}".Trim();
                    if (models.TryAdd(id, new Model
                    {
                        Id = id.ToModelId(GetIdentifier()), Name = name, OwnedBy = "Skyvern", Type = "chat",
                        Created = DateTimeOffset.TryParse(created, out var date) ? date.ToUnixTimeSeconds() : null,
                        Description = description, Tags = ["agent", Flag(agent, "is_saved_task") ? "saved-task" : "workflow"]
                    })) newIds++;
                }
                if (agents.Count < pageSize) break;
                if (newIds == 0) throw new InvalidOperationException("Skyvern agent pagination made no progress.");
            }
            return models.Values.ToList();
        }, baseTtl: TimeSpan.FromMinutes(15), jitterMinutes: 2, cancellationToken: cancellationToken);
    }
}
