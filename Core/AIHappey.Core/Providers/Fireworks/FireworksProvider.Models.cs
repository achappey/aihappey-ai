using AIHappey.Core.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Fireworks;

public partial class FireworksProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));

}
