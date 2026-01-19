using AIHappey.Core.Models;

namespace AIHappey.Core.ModelProviders;

public interface IAIModelProviderResolver
{
    Task<IModelProvider> Resolve(
        string model,
        CancellationToken ct = default);

    IModelProvider GetProvider();

    Task<ModelResponse> ResolveModels(
        CancellationToken ct);
}
