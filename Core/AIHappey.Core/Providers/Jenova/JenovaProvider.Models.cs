using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Jenova;

public sealed partial class JenovaProvider
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private async Task<IEnumerable<Model>> ListJenovaModelsAsync(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await _memoryCache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            using var agentsResponse = await SendAsync(HttpMethod.Get, "agents", null, key, ct);
            using var agentsDocument = JsonDocument.Parse(await agentsResponse.Content.ReadAsStringAsync(ct));
            using var modelsResponse = await SendAsync(HttpMethod.Get, "models", null, key, ct);
            using var modelsDocument = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync(ct));
            if (Property(agentsDocument.RootElement, "agents").ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Jenova agent catalog must contain an agents array.");
            if (Property(modelsDocument.RootElement, "models").ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Jenova model catalog must contain a models array.");

            var result = new List<Model>();
            foreach (var agent in Array(agentsDocument.RootElement, "agents"))
            {
                var slug = String(agent, "agent");
                if (string.IsNullOrWhiteSpace(slug))
                    continue;

                result.Add(new Model
                {
                    Id = $"agent/{slug}".ToModelId(GetIdentifier()),
                    Name = String(agent, "display_name") ?? slug,
                    Description = String(agent, "description"),
                    OwnedBy = "Jenova",
                    Type = "language",
                    Tags = ["agent"]
                });
            }

            foreach (var model in Array(modelsDocument.RootElement, "models"))
            {
                var id = String(model, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                var thinkingVariant = String(model, "thinking_variant");
                result.Add(new Model
                {
                    Id = $"model/{id}".ToModelId(GetIdentifier()),
                    Name = String(model, "name") ?? id,
                    Description = thinkingVariant is null ? null : $"Reasoning variant: {thinkingVariant}",
                    OwnedBy = "Jenova",
                    Type = "language",
                    Tags = thinkingVariant is null ? ["model"] : ["model", "reasoning"]
                });
            }

            return (IEnumerable<Model>)result;
        }, baseTtl: TimeSpan.FromMinutes(15), jitterMinutes: 2, cancellationToken: cancellationToken);
    }
}
