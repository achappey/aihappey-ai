using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.JSON2Video;

public partial class JSON2VideoProvider
{
    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedWithVideoAsync(request, UnsupportedUnifiedAsync, cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedWithVideoAsync(request, UnsupportedUnifiedStream, cancellationToken);

    private static Task<AIResponse> UnsupportedUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException($"JSON2Video model '{request.Model}' is not a video model.");

    private static IAsyncEnumerable<AIStreamEvent> UnsupportedUnifiedStream(AIRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException($"JSON2Video model '{request.Model}' is not a video model.");
}
