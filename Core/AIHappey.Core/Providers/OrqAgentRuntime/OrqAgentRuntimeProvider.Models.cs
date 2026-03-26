using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.OrqAgentRuntime;

public partial class OrqAgentRuntimeProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async _ =>
            {
                ApplyAuthHeader();

                var deployments = await ListDeploymentsInternalAsync(cancellationToken);
                var models = new List<Model>();

                foreach (var deployment in deployments)
                {
                    if (deployment is null
                        || string.IsNullOrWhiteSpace(deployment.Key)
                        || !IsChatCapableDeployment(deployment))
                        continue;

                    models.Add(new Model
                    {
                        Id = deployment.Key.ToModelId(GetIdentifier()),
                        Name = deployment.Key,
                        Description = string.IsNullOrWhiteSpace(deployment.Description) ? null : deployment.Description,
                        OwnedBy = ProviderName,
                        Created = ParseUnixTime(deployment.Created),
                        Type = MapDeploymentModelType(deployment.PromptConfig?.ModelType),
                        Tags = BuildDeploymentTags(deployment)
                    });
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}
