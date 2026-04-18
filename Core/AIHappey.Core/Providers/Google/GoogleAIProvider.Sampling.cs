using ModelContextProtocol.Protocol;
using AIHappey.Core.AI;
using AIHappey.Sampling.Mapping;
using AIHappey.Interactions.Mapping;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var interactionRequest = chatRequest.ToUnifiedRequest(GetIdentifier()).ToInteractionRequest(GetIdentifier());
        interactionRequest.Stream = true;
        interactionRequest.Store = false;
        this.SetDefaultInteractionProperties(interactionRequest);

        var result = await this.GetInteraction(interactionRequest,
                 cancellationToken);

        return result.ToUnifiedResponse(GetIdentifier()).ToSamplingResult();
    }

}
