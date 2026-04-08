using AIHappey.Unified.Models;

namespace AIHappey.Core.Contracts;

/// <summary>
/// Optional capability for providers that can execute unified requests directly.
/// Existing providers can adopt this gradually.
/// </summary>
public interface IUnifiedModelProvider
{
    Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default);
}

