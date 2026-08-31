using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Runway;

public partial class RunwayProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => await this.IsVideoModelAsync(request.Model, cancellationToken)
            ? await this.ExecuteUnifiedVideoAsync(request, cancellationToken: cancellationToken)
            : await this.IsSpeechModelAsync(request.Model, cancellationToken)
            ? await this.ExecuteUnifiedSpeechAsync(request, cancellationToken: cancellationToken)
            : await this.IsImageModelAsync(request.Model, cancellationToken)
            ? await this.ExecuteUnifiedImageAsync(request, cancellationToken)
            : await UnsupportedUnifiedAsync(request, cancellationToken);

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stream = await this.IsVideoModelAsync(request.Model, cancellationToken)
            ? this.StreamUnifiedVideoAsync(request, cancellationToken: cancellationToken)
            : await this.IsSpeechModelAsync(request.Model, cancellationToken)
            ? this.StreamUnifiedSpeechAsync(request, cancellationToken: cancellationToken)
            : await this.IsImageModelAsync(request.Model, cancellationToken)
            ? this.StreamUnifiedImageAsync(request, cancellationToken)
            : UnsupportedUnifiedStream(request, cancellationToken);
        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return streamEvent;
    }

    private static Task<AIResponse> UnsupportedUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException($"Runway model '{request.Model}' is not a video, speech, or image model.");

    private static IAsyncEnumerable<AIStreamEvent> UnsupportedUnifiedStream(AIRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException($"Runway model '{request.Model}' is not a video, speech, or image model.");
}
