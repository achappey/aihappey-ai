using AIHappey.Core.AI;
using AIHappey.Core.Models;
using MIS = Mistral.SDK;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
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
                ApplyAuthHeader();

                var client = new MIS.MistralClient(
                  _keyResolver.Resolve(GetIdentifier()),
                  _client
                );

                var models = await client.Models
                    .GetModelsAsync(cancellationToken: ct);

                var agents = await ListAgentsAsync(ct);

                List<Model> imageModels = [new Model()
                        {
                            Id = "mistral-medium-latest".ToModelId(GetIdentifier()),
                            Name = "mistral-medium-latest",
                            OwnedBy = GetName(),
                            Type = "image"
                        }, new Model()
                        {
                            Id = "mistral-large-latest".ToModelId(GetIdentifier()),
                            Name = "mistral-large-latest",
                            OwnedBy = GetName(),
                            Type = "image"
                        }];

                var agentModels = agents
                    .Where(a => !string.IsNullOrWhiteSpace(a.Id))
                    .Select(a => new Model()
                    {
                        Id = $"{AgentModelPrefix}{a.Id}".ToModelId(GetIdentifier()),
                        Name = string.IsNullOrWhiteSpace(a.Name) ? $"{AgentModelPrefix}{a.Id}" : a.Name!,
                        Description = string.IsNullOrWhiteSpace(a.Description)
                            ? (string.IsNullOrWhiteSpace(a.Model) ? null : $"Mistral agent backed by {a.Model}")
                            : a.Description,
                        OwnedBy = GetName(),
                        Type = "language",
                        Created = a.CreatedAt?.ToUnixTimeSeconds(),
                        Tags = string.IsNullOrWhiteSpace(a.Model)
                            ? ["agent"]
                            : ["agent", a.Model!]
                    });

                return models.Data
                    .Select(a => new Model()
                    {
                        Id = a.Id.ToModelId(GetIdentifier()),
                        Name = a.Id,
                        OwnedBy = GetName(),
                    })
                    .Concat(imageModels)
                    .Concat(agentModels)
                    .OrderByDescending(a => a.Created ?? 0);
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }


}
