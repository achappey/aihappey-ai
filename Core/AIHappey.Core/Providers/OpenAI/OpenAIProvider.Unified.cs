using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => request.Model?.Contains("search-preview") == true
         ? this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken)
        : this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => request.Model?.Contains("search-preview") == true
         ? this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken)
         : this.StreamUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);
}
