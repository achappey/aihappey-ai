using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LLMGateway;

public partial class LLMGatewayProvider
{
    public Task<OpenAIDecisionResponse> ExecuteOpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleDecisionRequestAsync(request, cancellationToken: cancellationToken);
    }

    public Task<DecisionResponse> ExecuteDecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleVercelDecisionRequestAsync(
            request, GetIdentifier(), cancellationToken: cancellationToken);
    }
}
