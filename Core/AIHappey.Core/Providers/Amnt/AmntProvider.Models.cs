using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Amnt;

public partial class AmntProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_keys.Resolve(GetIdentifier()))) return [];
        return await _cache.GetOrCreateAsync(this.GetCacheKey(_keys.Resolve(GetIdentifier())!), async token =>
        {
            var models = new List<Model>
            {
                new() { Id = "amnt-svg-1.1".ToModelId(GetIdentifier()), Name = "AMNT SVG 1.1",
                    OwnedBy = "Amnt", Type = "image", Tags = ["svg"] },
                new() { Id = "svg/vectorize".ToModelId(GetIdentifier()), Name = "AMNT SVG vectorize",
                    Description = "Route: POST /api/v1/svg/vectorize; provide one image.",
                    OwnedBy = "Amnt", Type = "image", Tags = ["svg", "vectorize"] }
            };
            for (var offset = 0; ; offset += 100)
            {
                var (body, _) = await SendAsync(HttpMethod.Get, $"api/v1/agents?limit=100&offset={offset}", null, token);
                if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("agents", out var agents)
                    || agents.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("Amnt agent catalog has no agents array.");
                foreach (var agent in agents.EnumerateArray())
                {
                    var id = String(agent, "agent");
                    if (!ValidAgentId(id)) continue;
                    models.Add(new Model
                    {
                        Id = $"agents/{id}".ToModelId(GetIdentifier()), Name = String(agent, "name") ?? id!,
                        Description = String(agent, "description"), OwnedBy = "Amnt", Type = "language",
                        Tags = ["agent", String(agent, "connector") ?? "unknown"]
                    });
                }
                var total = body.TryGetProperty("total", out var count) && count.TryGetInt32(out var number) ? number : 0;
                if (agents.GetArrayLength() == 0 || offset + 100 >= total) break;
            }
            return (IEnumerable<Model>)models.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }, baseTtl: TimeSpan.FromMinutes(15), jitterMinutes: 2, cancellationToken: ct);
    }

    private static bool ValidAgentId(string? id)
    {
        var parts = id?.Split('/');
        return parts is { Length: 2 } && parts.All(part => part.Length > 0
            && part.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'));
    }
}
