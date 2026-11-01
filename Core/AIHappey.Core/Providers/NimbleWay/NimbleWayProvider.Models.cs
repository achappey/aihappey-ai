using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.NimbleWay;

public partial class NimbleWayProvider
{
    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var models = (await this.ListModels(_keyResolver.Resolve(GetIdentifier()))).ToList();
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return models;

        const int limit = 200;
        var offset = 0;
        while (true)
        {
            var page = await SendJsonAsync(HttpMethod.Get, $"v2/agents?limit={limit}&offset={offset}", null, cancellationToken);
            if (!page.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("NimbleWay agent list is missing its items array.");

            foreach (var agent in items.EnumerateArray())
            {
                var id = RequiredString(agent, "id");
                if (agent.TryGetProperty("is_active", out var active) && active.ValueKind == JsonValueKind.False)
                    continue;
                var fullId = $"{GetIdentifier()}/{AgentModelPrefix}{id}";
                if (models.Any(model => string.Equals(model.Id, fullId, StringComparison.Ordinal)))
                    continue;
                models.Add(new Model
                {
                    Id = fullId,
                    Name = String(agent, "display_name") ?? String(agent, "agent_name") ?? id,
                    Description = String(agent, "description"),
                    OwnedBy = nameof(NimbleWay),
                    Type = "language",
                    Tags = ["agent", "dynamic"]
                });
            }

            var count = items.GetArrayLength();
            offset += count;
            if (count == 0 || offset >= page.GetProperty("total").GetInt32())
                break;
        }

        return models;
    }
}
