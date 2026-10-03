using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Manus;

public sealed partial class ManusProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        var api = new ManusApiClient(_http, _transfers, key);
        return await _cache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var models = new List<Model>();
            foreach (var profile in new[] { "standard", "lite", "max" })
                models.Add(new Model { Id = "manus/" + profile, Name = "Manus " + profile, OwnedBy = "manus", Type = "language", Tags = ["agent"] });
            foreach (var agent in Array(await api.Agents(ct), "data"))
                if (S(agent, "id") is { Length: > 0 } id)
                    models.Add(new Model { Id = "manus/agents/" + id, Name = S(agent, "nickname") ?? id,
                        Description = S(agent, "about"), OwnedBy = "manus", Type = "language", Tags = ["agent"] });
            return (IEnumerable<Model>)models.DistinctBy(m => m.Id).ToList();
        }, baseTtl: TimeSpan.FromMinutes(5), jitterMinutes: 1, cancellationToken: cancellationToken);
    }
}
