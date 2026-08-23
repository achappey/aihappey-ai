using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Zenlayer;

public partial class ZenlayerProvider
{

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

}