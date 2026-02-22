using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Picsart;

public partial class PicsartProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));
}
