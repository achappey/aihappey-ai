using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Tembo;

public partial class TemboProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        return await _memoryCache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var result = new List<Model>();
            foreach (var agent in await ListItemsAsync(key, "v1/agents?archived=false", ct))
            {
                if (Property(agent, "archivedAt") is { ValueKind: JsonValueKind.String }) continue;
                var id = RequiredString(agent, "id");
                if (!Guid.TryParse(id, out _)) throw new InvalidOperationException("Tembo returned an invalid agent UUID.");
                result.Add(new Model
                {
                    Id = $"tembo/agents/{id}", Name = String(agent, "name") ?? id,
                    OwnedBy = "Tembo", Type = "language", Tags = ["agent"],
                    Description = "Configured Tembo agent. Currently supports explicit queue-only submission."
                });
            }
            var models = (await ListItemsAsync(key, "v1/models", ct))
                .ToDictionary(model => RequiredString(model, "name"), StringComparer.Ordinal);
            foreach (var runtime in await ListItemsAsync(key, "v1/runtimes", ct))
            {
                if (!Available(runtime)) continue;
                var harness = RequiredString(runtime, "name");
                if (Property(runtime, "compatibleModels") is not { ValueKind: JsonValueKind.Array } compatible)
                    throw new InvalidOperationException("Tembo runtime is missing compatibleModels.");
                foreach (var reference in compatible.EnumerateArray())
                {
                    var name = RequiredString(reference, "name");
                    if (!Available(reference) || !models.TryGetValue(name, out var model)
                        || !IsTrue(model, "enabled") || !IsTrue(model, "available")) continue;
                    result.Add(new Model
                    {
                        Id = $"tembo/{harness}:{name}",
                        Name = $"{String(reference, "label") ?? name} ({String(runtime, "displayName") ?? harness})",
                        OwnedBy = String(reference, "provider") ?? "Tembo", Type = "language", Tags = ["agent"]
                    });
                }
            }
            return (IEnumerable<Model>)result.DistinctBy(model => model.Id).ToList();
        }, baseTtl: TimeSpan.FromMinutes(5), jitterMinutes: 1, cancellationToken: cancellationToken);
    }

    private static bool Available(JsonElement value)
    {
        var status = Property(value, "availability") is { } availability ? String(availability, "status") : null;
        return status switch
        {
            "available" => true,
            "unavailable" => false,
            _ => throw new NotSupportedException($"Tembo availability status '{status ?? "missing"}' is unsupported.")
        };
    }
}
