using AIHappey.Unified.Models;

namespace AIHappey.Core.Contracts;

[Obsolete("IUnifiedModelProvider interface is deprecated. Do not implement or use this interface in new code. Use the unified models directly within providers without requiring this interface.")]
public interface IUnifiedModelProvider
{
    Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default);
}

