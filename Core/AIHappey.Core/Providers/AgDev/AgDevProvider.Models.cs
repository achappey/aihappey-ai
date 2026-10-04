using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.AgDev;

public partial class AgDevProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier()))) return [];
        var transport = GetTransport();
        return await _memoryCache.GetOrCreateAsync(this.GetCacheKey(transport.Key), async ct =>
        {
            var reply = await SendJson(transport, HttpMethod.Get, "agents", null, ct);
            if (reply.Raw.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("AgDev agent catalog must be an array.");
            return reply.Raw.EnumerateArray().Select(agent =>
            {
                var id = String(agent, "id") ?? throw new InvalidOperationException("AgDev agent has no id.");
                var name = String(agent, "name");
                return new Model
                {
                    Id = id.ToModelId(GetIdentifier()), Name = string.IsNullOrWhiteSpace(name) ? id : name,
                    OwnedBy = "AgDev", Type = "chat",
                    Description = $"{String(agent, "description")}\nInput: strict JSON object passed directly to AgDev. Every request starts a new run.".Trim(),
                    Created = DateTimeOffset.TryParse(String(agent, "createdAt"), out var date) ? date.ToUnixTimeSeconds() : null,
                    Tags = Flag(agent, "hasChanges")
                        ? ["agent", Flag(agent, "isPublished") ? "published" : "unpublished", "unpublished-changes"]
                        : ["agent", Flag(agent, "isPublished") ? "published" : "unpublished"]
                };
            }).DistinctBy(m => m.Id).ToList();
        }, baseTtl: TimeSpan.FromMinutes(15), jitterMinutes: 2, cancellationToken: cancellationToken);
    }
}
