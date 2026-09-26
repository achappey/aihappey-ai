using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Runtype;

public sealed partial class RuntypeProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        return await _cache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var models = new List<Model>
            {
                OperationModel("agents", "List Runtype agents (raw paginated response)"),
                OperationModel("executions/status", "Read Runtype asynchronous execution status")
            };
            var cursor = (string?)null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var path = "v1/agents?limit=100" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
                var page = (await SendJson(HttpMethod.Get, path, null, ct)).Body;
                if (Property(page, "data") is not { ValueKind: JsonValueKind.Array } data) break;
                foreach (var agent in data.EnumerateArray())
                {
                    var id = String(agent, "id");
                    if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || !seen.Add(id)) continue;
                    var name = String(agent, "name") ?? id;
                    var description = String(agent, "description");
                    var created = DateTimeOffset.TryParse(String(agent, "createdAt"), out var date) ? date.ToUnixTimeSeconds() : (long?)null;
                    models.Add(new Model { Id = $"agents/{id}".ToModelId(GetIdentifier()), Name = name,
                        Description = description, Created = created, Type = "language", OwnedBy = "Runtype",
                        Tags = ["agent"] });
                    models.Add(OperationModel($"agents/{id}/execution", $"Read execution detail for {name}"));
                    models.Add(OperationModel($"agents/{id}/events", $"Replay and tail execution events for {name}"));
                }
                var pagination = Property(page, "pagination");
                cursor = pagination is { } p && Property(p, "hasMore") is { ValueKind: JsonValueKind.True }
                    ? String(p, "nextCursor") : null;
            } while (cursor is not null && seen.Add("cursor:" + cursor));
            return (IEnumerable<Model>)models;
        }, baseTtl: TimeSpan.FromMinutes(15), jitterMinutes: 2, cancellationToken: cancellationToken);
    }

    private Model OperationModel(string route, string description) => new()
    {
        Id = route.ToModelId(GetIdentifier()), Name = description, Description = description,
        Type = "language", OwnedBy = "Runtype", Tags = ["agent"]
    };
}
