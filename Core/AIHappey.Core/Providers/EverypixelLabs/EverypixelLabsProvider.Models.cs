using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.EverypixelLabs;

public partial class EverypixelLabsProvider
{
    private Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return Task.FromResult<IEnumerable<Model>>([]);

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IEnumerable<Model>>(GetIdentifier().GetModels());
    }
}

