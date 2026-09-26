using System.Net;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Ainvented;

public sealed partial class AinventedProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];

        return await _cache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var result = new List<Model>();
            var models = await SendJsonAsync(HttpMethod.Get, "models", null, ct);
            if (Property(models, "data") is { ValueKind: JsonValueKind.Array } data)
                foreach (var item in data.EnumerateArray())
                {
                    var id = String(item, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    result.Add(new Model
                    {
                        Id = id.ToModelId(GetIdentifier()), Name = id, Type = "language",
                        OwnedBy = String(item, "owned_by") ?? "ainvented",
                        Created = Property(item, "created") is { ValueKind: JsonValueKind.Number } created ? created.GetInt64() : null
                    });
                }

            result.Add(new Model
            {
                Id = "workflow".ToModelId(GetIdentifier()), Name = "Ainvented workflow",
                Type = "language", OwnedBy = "ainvented", Tags = ["agent"],
                Description = "Execute the workflow project selected by the API key. Requires a workflow-project key."
            });

            // The Tasks feature may be disabled for a chat project. This does not affect model listing.
            string? after = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var path = "tasks?limit=100" + (after is null ? "" : $"&after={Uri.EscapeDataString(after)}");
                JsonElement tasks;
                try { tasks = await SendJsonAsync(HttpMethod.Get, path, null, ct); }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) { break; }
                var array = Property(tasks, "data") ?? Property(tasks, "tasks");
                if (array is not { ValueKind: JsonValueKind.Array }) break;
                foreach (var task in array.Value.EnumerateArray())
                {
                    var id = String(task, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (Property(task, "enabled") is { ValueKind: JsonValueKind.False }) continue;
                    result.Add(new Model
                    {
                        Id = $"task/{id}".ToModelId(GetIdentifier()), Name = String(task, "name") ?? id,
                        Description = String(task, "instructions") ?? "Run Ainvented task synchronously.",
                        OwnedBy = "ainvented", Type = "language", Tags = ["agent"]
                    });
                }
                after = String(tasks, "next_cursor") ?? String(tasks, "next_after");
                if (after is null && Property(tasks, "has_more") is { ValueKind: JsonValueKind.True }
                    && array.Value.GetArrayLength() > 0)
                    after = String(array.Value[array.Value.GetArrayLength() - 1], "id");
            } while (after is not null && seen.Add(after));

            return (IEnumerable<Model>)result.DistinctBy(m => m.Id).ToList();
        }, baseTtl: TimeSpan.FromMinutes(15), jitterMinutes: 2, cancellationToken: cancellationToken);
    }
}
