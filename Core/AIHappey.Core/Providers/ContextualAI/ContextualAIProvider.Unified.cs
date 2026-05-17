using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.ContextualAI;

public partial class ContextualAIProvider
{
    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => IsContextualAIAgentModel(request.Model)
            ? ExecuteAgentUnifiedAsync(request, cancellationToken)
            : ExecuteGenerateUnifiedAsync(request, cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => IsContextualAIAgentModel(request.Model)
            ? StreamAgentUnifiedAsync(request, cancellationToken)
            : StreamGenerateUnifiedAsync(request, cancellationToken);
}
