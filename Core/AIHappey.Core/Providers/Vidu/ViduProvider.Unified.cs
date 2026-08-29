using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Vidu;

public partial class ViduProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => await this.IsVideoModelAsync(request.Model, cancellationToken)
            ? await this.ExecuteUnifiedVideoAsync(request, cancellationToken: cancellationToken)
            : await this.IsSpeechModelAsync(request.Model, cancellationToken)
            ? await this.ExecuteUnifiedSpeechAsync(request, cancellationToken: cancellationToken)
            : await UnsupportedUnifiedAsync(request, cancellationToken);

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stream = await this.IsVideoModelAsync(request.Model, cancellationToken)
            ? this.StreamUnifiedVideoAsync(request, cancellationToken: cancellationToken)
            : await this.IsSpeechModelAsync(request.Model, cancellationToken)
            ? this.StreamUnifiedSpeechAsync(request, cancellationToken: cancellationToken)
            : UnsupportedUnifiedStream(request, cancellationToken);
        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return streamEvent;
    }

    private static Task<AIResponse> UnsupportedUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException($"Vidu model '{request.Model}' is not a video or speech model.");

    private static IAsyncEnumerable<AIStreamEvent> UnsupportedUnifiedStream(AIRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException($"Vidu model '{request.Model}' is not a video or speech model.");
}
