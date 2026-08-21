using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.AgentSky;

public partial class AgentSkyProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(),
            async ct =>
            {
                ApplyAuthHeader();
                using var response = await _client.GetAsync("v1/agents", ct);
                await EnsureAgentSkySuccessAsync(response, "list agents", ct);
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!TryGetProperty(document.RootElement, "agents", out var agents)
                    || agents.ValueKind != JsonValueKind.Array)
                    return [];

                var models = new List<Model>();
                foreach (var agent in agents.EnumerateArray())
                {
                    var slug = TryGetString(agent, "slug");
                    if (string.IsNullOrWhiteSpace(slug) || TryGetBoolean(agent, "archived") == true)
                        continue;

                    var name = TryGetString(agent, "displayName")
                               ?? TryGetString(agent, "name")
                               ?? slug;
                    var agentType = TryGetString(agent, "agentType");
                    var llm = TryGetString(agent, "llm");
                    var createdAt = TryGetString(agent, "createdAt");

                    models.Add(new Model
                    {
                        Id = slug.ToModelId(GetIdentifier()),
                        Name = name,
                        Type = "chat",
                        OwnedBy = string.IsNullOrWhiteSpace(agentType) ? nameof(AgentSky) : agentType,
                        Description = string.IsNullOrWhiteSpace(llm)
                            ? $"AgentSky agent '{slug}'."
                            : $"AgentSky {agentType ?? "agent"} agent '{slug}' using {llm}.",
                        Created = DateTimeOffset.TryParse(createdAt, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var created) ? created.ToUnixTimeSeconds() : null,
                        Tags = new[] { "agent", agentType, llm }
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Select(value => value!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                    });
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(1),
            jitterMinutes: 30,
            cancellationToken: cancellationToken);
    }
}
