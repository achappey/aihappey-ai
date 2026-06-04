using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Hyperbrowser;

public partial class HyperbrowserProvider
{
    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => ExecuteHyperbrowserTaskUnifiedAsync(request, cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => StreamHyperbrowserTaskUnifiedAsync(request, cancellationToken);
}
