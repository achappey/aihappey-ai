using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Vapi;

public partial class VapiProvider
{
    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var models = GetIdentifier()
            .GetModels();

        return await Task.FromResult<IEnumerable<Model>>(models);
    }
}

