using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Portkey;

public partial class PortkeyProvider
{
  public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
     => throw new NotSupportedException();

}