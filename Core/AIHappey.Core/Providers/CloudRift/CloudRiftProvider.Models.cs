using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.CloudRift;

public sealed partial class CloudRiftProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await this.ListModels(keyResolver.Resolve(GetIdentifier()));
}

