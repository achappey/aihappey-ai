using AIHappey.Core.AI;
using AIHappey.Interactions.Extensions;
using AIHappey.Interactions;
using AIHappey.Abstractions.Http;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async IAsyncEnumerable<InteractionStreamEventPart> GetInteractions(InteractionRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default,
        ProviderBackendCaptureRequest? capture = null)
    {

        ApplyAuthHeader();

        this.SetDefaultInteractionProperties(request);
        capture ??= request.GetGoogleBackendCapture(GetIdentifier());

        if (TryNormalizeGoogleAgentRequest(request, out _, stream: true))
        {
            string? interactionId = null;

            try
            {
                await foreach (var update in CreateGoogleAgentInteractionStream(request, cancellationToken, capture))
                {
                    if (update is InteractionCreatedEvent { Interaction.Id: not null } start)
                        interactionId = start.Interaction.Id;
                    else if (update is InteractionCompletedEvent { Interaction.Id: not null } complete)
                        interactionId ??= complete.Interaction.Id;

                    yield return update;
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(interactionId))
                    await DeleteGoogleAgentInteraction(interactionId, cancellationToken);
            }

            yield break;
        }

        request.Stream = true;
        request.Store = false;

        await foreach (var update in _client.GetInteractions(
                           request,
                           GetIdentifier(),
                           capture: capture,
                           ct: cancellationToken))
        {
            yield return update;
        }

    }

    public async Task<Interaction> GetInteraction(InteractionRequest request,
           CancellationToken cancellationToken = default,
           ProviderBackendCaptureRequest? capture = null)
    {

        ApplyAuthHeader();

        this.SetDefaultInteractionProperties(request);
        capture ??= request.GetGoogleBackendCapture(GetIdentifier());

        if (TryNormalizeGoogleAgentRequest(request, out _))
        {
            string? interactionId = null;
            var initialInteraction = await CreateGoogleAgentInteraction(request, cancellationToken, capture);
            interactionId = initialInteraction.Id;

            try
            {
                if (string.IsNullOrWhiteSpace(interactionId))
                    throw new InvalidOperationException("Google agent interaction create response did not include an id.");

                return await PollGoogleAgentInteraction(interactionId, cancellationToken);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(interactionId))
                    await DeleteGoogleAgentInteraction(interactionId, cancellationToken);
            }
        }

        request.Stream = false;
        request.Store = false;

        return await _client.GetInteraction(
                            request,
                            GetIdentifier(),
                            capture: capture,
                            ct: cancellationToken);

    }

}
