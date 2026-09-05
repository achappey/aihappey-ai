using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.ShadowOS;

public partial class ShadowOSProvider
{
    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => this.ListModels(_keyResolver.Resolve(GetIdentifier()));
}
