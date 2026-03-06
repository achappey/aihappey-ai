using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.NimbleWay;

public partial class NimbleWayProvider
{
    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        var staticModels = (await this.ListModels(_keyResolver.Resolve(GetIdentifier()))).ToList();

        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return staticModels;

        ApplyAuthHeader();

        var dynamicAgents = await ListAllAgentsAsync(cancellationToken);
        foreach (var agent in dynamicAgents)
        {
            if (string.IsNullOrWhiteSpace(agent.Name))
                continue;

            var localId = $"{AgentModelPrefix}{agent.Name}";
            var fullId = localId.ToModelId(GetIdentifier());

            if (staticModels.Any(m => string.Equals(m.Id, fullId, StringComparison.OrdinalIgnoreCase)))
                continue;

            staticModels.Add(new Model
            {
                Id = fullId,
                Name = string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.Name! : agent.DisplayName!,
                Description = agent.Description,
                OwnedBy = string.IsNullOrWhiteSpace(agent.ManagedBy) ? nameof(NimbleWay) : agent.ManagedBy!,
                Type = "language",
                Tags = ["agent", "dynamic"]
            });
        }

        return staticModels;
    }

    private async Task<List<NimbleWayAgentInfo>> ListAllAgentsAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 250;
        var offset = 0;
        var all = new List<NimbleWayAgentInfo>();

        while (true)
        {
            var page = await ListAgentsPageAsync(offset, pageSize, cancellationToken);
            if (page.Count == 0)
                break;

            all.AddRange(page);
            if (page.Count < pageSize)
                break;

            offset += pageSize;
        }

        return all;
    }

    private async Task<List<NimbleWayAgentInfo>> ListAgentsPageAsync(int offset, int limit, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"v1/agents?privacy=all&limit={limit}&offset={offset}");
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"NimbleWay list agents failed ({(int)resp.StatusCode}): {body}");

        var page = JsonSerializer.Deserialize<List<NimbleWayAgentInfo>>(body, JsonSerializerOptions.Web);
        return page ?? [];
    }
}

