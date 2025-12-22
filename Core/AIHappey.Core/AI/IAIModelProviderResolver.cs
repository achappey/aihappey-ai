using AIHappey.Core.Models;

namespace AIHappey.Core.AI;

public interface IAIModelProviderResolver
{
    Task<IModelProvider> Resolve(
        string model,
        CancellationToken ct = default);

    IModelProvider GetProvider();

    Task<ModelReponse> ResolveModels(
        CancellationToken ct);
}
