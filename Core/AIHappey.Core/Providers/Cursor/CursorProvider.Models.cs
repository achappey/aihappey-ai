using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Cursor;

public sealed partial class CursorProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        var api = new CursorApiClient(_http, key); // Freeze the exact credential used for this cache entry.
        return await _cache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var models = new List<Model> { new() { Id = "cursor/default", Name = "Cursor Cloud Agent", OwnedBy = "cursor", Type = "language", Tags = ["agent"] } };
            var raw = await api.ListModelsAsync(ct);
            foreach (var model in Items(raw))
                if (Str(model, "id") is { } id)
                    models.Add(new Model { Id = "cursor/models/" + id, Name = Str(model, "displayName") ?? id,
                        Description = Str(model, "description"), OwnedBy = "cursor", Type = "language", Tags = ["agent"] });
            string? cursor = null;
            var seen = new HashSet<string>();
            do
            {
                var agents = await api.ListAgentsAsync(new { includeArchived = false, cursor }, ct);
                foreach (var agent in Items(agents))
                    if (Str(agent, "id") is { } id && Str(agent, "status") != "ARCHIVED")
                        models.Add(new Model { Id = "cursor/agents/" + id, Name = Str(agent, "name") ?? id,
                            Description = "Existing Cursor agent; subsequent chat creates a new run.", OwnedBy = "cursor", Type = "language", Tags = ["agent"] });
                cursor = Str(agents, "nextCursor");
                if (cursor is not null && !seen.Add(cursor)) throw new InvalidOperationException("Cursor agents pagination repeated a cursor.");
            } while (!string.IsNullOrEmpty(cursor));
            return (IEnumerable<Model>)models.DistinctBy(m => m.Id).ToList();
        }, baseTtl: TimeSpan.FromMinutes(5), jitterMinutes: 1, cancellationToken: cancellationToken);
    }
    private static IEnumerable<JsonElement> Items(JsonElement raw) => Prop(raw, "items") is { ValueKind: JsonValueKind.Array } items
        ? items.EnumerateArray().ToArray() : [];
}
