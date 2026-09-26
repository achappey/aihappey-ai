using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Connect0;

public sealed partial class Connect0Provider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var token = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(token)) return [];
        return await _cache.GetOrCreateAsync(this.GetCacheKey(token), async ct =>
        {
            var accounts = (await SendJson(HttpMethod.Get, "v1/accounts", null, ct)).Raw;
            var result = new List<Model>();
            if (Property(accounts, "items") is not { ValueKind: JsonValueKind.Array } accountItems)
                throw new InvalidOperationException("Connect0 accounts response has no items array.");

            foreach (var account in accountItems.EnumerateArray())
            {
                var accountSlug = String(account, "slug");
                if (accountSlug is null || !AccountSlug.IsMatch(accountSlug)) continue;
                result.Add(OperationModel($"{accountSlug}/agents", $"List agents in Connect0 account {accountSlug}"));
                var agents = (await SendJson(HttpMethod.Get, $"v1/accounts/{Enc(accountSlug)}/agents", null, ct)).Raw;
                if (Property(agents, "items") is not { ValueKind: JsonValueKind.Array } items)
                    throw new InvalidOperationException("Connect0 agents response has no items array.");

                foreach (var agent in items.EnumerateArray())
                {
                    var slug = String(agent, "slug");
                    if (slug is null || !AgentSlug.IsMatch(slug)) continue;
                    var prefix = $"{accountSlug}/agents/{slug}";
                    result.Add(OperationModel($"{prefix}/detail", $"Read Connect0 agent {slug}"));
                    if (String(agent, "status") is "revoked") continue;
                    result.Add(new Model
                    {
                        Id = prefix.ToModelId(GetIdentifier()),
                        Name = String(agent, "display_name") ?? slug,
                        Description = String(agent, "description"),
                        Created = DateTimeOffset.TryParse(String(agent, "installed_at"), out var installed)
                            ? installed.ToUnixTimeSeconds() : null,
                        OwnedBy = "Connect0", Type = "language", Tags = ["agent", "tools"]
                    });
                    foreach (var (suffix, description) in new[]
                    {
                        ("runs", "List runs"), ("runs/detail", "Read a run"),
                        ("runs/cancel", "Cancel a run"), ("runs/replay", "Replay a run"),
                        ("runs/stream", "Stream a run"), ("threads", "List threads"),
                        ("threads/messages", "Read thread messages")
                    }) result.Add(OperationModel($"{prefix}/{suffix}", $"{description} for Connect0 agent {slug}"));
                }
            }

            return (IEnumerable<Model>)result.DistinctBy(model => model.Id).ToList();
        }, baseTtl: TimeSpan.FromMinutes(5), jitterMinutes: 1, cancellationToken: cancellationToken);
    }

    private Model OperationModel(string route, string description) => new()
    {
        Id = route.ToModelId(GetIdentifier()), Name = description, Description = description,
        OwnedBy = "Connect0", Type = "language", Tags = ["agent", "operation"]
    };
}
