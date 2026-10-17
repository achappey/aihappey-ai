using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LLMGateway;

public partial class LLMGatewayProvider
{
    private Task<OpenAIDecisionResponse> ExecuteOpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleDecisionRequestAsync(request, cancellationToken: cancellationToken);
    }

    private Task<DecisionResponse> ExecuteDecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleVercelDecisionRequestAsync(
            request, GetIdentifier(), cancellationToken: cancellationToken);
    }
}
