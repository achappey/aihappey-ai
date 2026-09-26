using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Plori;

public sealed partial class PloriProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        return await _cache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var agents = await RestAsync(HttpMethod.Get, "agents", null, null, ct);
            return (IEnumerable<Model>)agents.EnumerateArray()
                .Where(a => Str(a, "id") is not null && Prop(a, "deleting") is not { ValueKind: JsonValueKind.True })
                .Select(a => new Model
                {
                    Id = Str(a, "id")!.ToModelId(GetIdentifier()), Name = Str(a, "name") ?? Str(a, "id")!,
                    Description = $"Plori agent ({Str(a, "status") ?? "unknown"}); configured model: {Str(a, "model") ?? "Plori Router"}.",
                    OwnedBy = "plori", Type = "language", Tags = ["agent", "tools", "stateful"]
                }).ToList();
        }, baseTtl: TimeSpan.FromMinutes(5), jitterMinutes: 1, cancellationToken: cancellationToken);
    }
}
