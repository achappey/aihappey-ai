using AIHappey.Core.Models;
using AIHappey.Core.AI;
using System.Text.Json;


namespace AIHappey.Core.Providers.ContextualAI;

public partial class ContextualAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var staticModels = (await this.ListModels(_keyResolver.Resolve(GetIdentifier()))).ToList();
                var key = _keyResolver.Resolve(GetIdentifier());

                if (string.IsNullOrWhiteSpace(key))
                    return staticModels;

                try
                {
                    staticModels.AddRange(await ListAgentModelsAsync(cancellationToken));
                }
                catch
                {
                    return staticModels;
                }

                return [.. staticModels.DistinctBy(model => model.Id)];

            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IEnumerable<Model>> ListAgentModelsAsync(CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var models = new List<Model>();
        string? cursor = null;

        do
        {
            var url = string.IsNullOrWhiteSpace(cursor)
                ? "v1/agents?limit=1000"
                : $"v1/agents?limit=1000&cursor={Uri.EscapeDataString(cursor)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _client.SendAsync(req, cancellationToken);

            if (!resp.IsSuccessStatusCode)
                return models;

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            if (root.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array)
            {
                foreach (var agent in agents.EnumerateArray())
                {
                    var id = agent.TryGetString("id");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var name = agent.TryGetString("name") ?? id;

                    models.Add(new Model
                    {
                        Id = $"agent/{id}".ToModelId(GetIdentifier()),
                        Name = name,
                        Description = agent.TryGetString("description") ?? $"ContextualAI agent '{name}'.",
                        OwnedBy = nameof(ContextualAI),
                        Type = "language",
                        Tags = ["agent", "shortcut", $"agent:{id}"]
                    });
                }
            }

            cursor = root.TryGetString("next_cursor");
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return models;
    }
}
