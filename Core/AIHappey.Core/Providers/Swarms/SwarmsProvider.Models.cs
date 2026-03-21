using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Swarms;

public partial class SwarmsProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var backendModels = await GetAvailableBackendModelsAsync(cancellationToken);

        return [.. backendModels.Select(backendModel => new Model
        {
            Id = backendModel.Id.ToModelId(GetIdentifier()),
            Object = "model",
            OwnedBy = backendModel.OwnedBy,
            Name = backendModel.Name,
            Description = null,
            ContextWindow = backendModel.ContextWindow,
            MaxTokens = backendModel.MaxTokens,
            Type = backendModel.Type,
            Tags = backendModel.Tags?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Pricing = backendModel.Pricing
        })];
    }
}
