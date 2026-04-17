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

        request.Stream = true;
        request.Store = false;

        this.SetDefaultInteractionProperties(request);

        await foreach (var update in _client.GetInteractions(
                           request,
                           ct: cancellationToken))
        {
            yield return update;
        }

    }

    public async Task<Interaction> GetInteraction(InteractionRequest request,
           CancellationToken cancellationToken = default)
    {

        ApplyAuthHeader();

        request.Stream = false;
        request.Store = false;

        this.SetDefaultInteractionProperties(request);

        return await _client.GetInteraction(
                            request,
                            ct: cancellationToken);

    }

}
