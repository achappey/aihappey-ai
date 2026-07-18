using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await IsTranscriptionModelAsync(request.Model, cancellationToken))
        {
            return await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken);
        }

        return request.Model?.Contains("search-preview") == true
            ? await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken)
            : await this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await IsTranscriptionModelAsync(request.Model, cancellationToken))
        {
            await foreach (var streamEvent in this.StreamUnifiedTranscriptionAsync(request, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return streamEvent;
            }

            yield break;
        }

        var stream = request.Model?.Contains("search-preview") == true
            ? this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken)
            : this.StreamUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);

        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return streamEvent;
    }

    private async Task<bool> IsTranscriptionModelAsync(
        string? modelId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var model = await this.GetModel(modelId, cancellationToken);
        return string.Equals(model.Type, "transcription", StringComparison.OrdinalIgnoreCase);
    }
}
