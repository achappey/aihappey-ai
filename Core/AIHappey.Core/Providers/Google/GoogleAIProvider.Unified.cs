using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using AIHappey.Interactions.Mapping;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await this.IsTranscriptionModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken);

        var interaction = await GetInteraction(
            request.ToInteractionRequest(GetIdentifier()),
            cancellationToken);

        return interaction.ToUnifiedResponse(GetIdentifier());
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await this.IsTranscriptionModelAsync(request.Model, cancellationToken))
        {
            await foreach (var streamEvent in this.StreamUnifiedTranscriptionAsync(
                               request,
                               cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return streamEvent;
            }

            yield break;
        }

        var interactionRequest = request.ToInteractionRequest(GetIdentifier());
        interactionRequest.Stream = true;
        interactionRequest.Store = false;
        this.SetDefaultInteractionProperties(interactionRequest);

        await foreach (var update in GetInteractions(
                           interactionRequest,
                           cancellationToken: cancellationToken))
        {
            foreach (var streamEvent in update.ToUnifiedStreamEvent(GetIdentifier()))
                yield return MarkGoogleAgentUnifiedToolEventProviderExecuted(streamEvent);
        }
    }
}
