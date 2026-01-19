using AIHappey.Core.ModelProviders;
using AIHappey.Core.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderExtensions
{
    public static async Task<Model> GetModel(this IModelProvider modelProvider,
      string? modelId,
      CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        var models = await modelProvider.ListModels(cancellationToken);
        var model = models.FirstOrDefault(a => a.Id.EndsWith(modelId))
            ?? throw new ArgumentException(modelId);

        return model;
    }

}
