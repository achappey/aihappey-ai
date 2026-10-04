using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using AIHappey.Interactions.Mapping;
using AIHappey.Interactions;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var interactionRequest = CreateGoogleUnifiedInteractionRequest(request);
        var isCustomAgent = IsCustomAgentSelection(interactionRequest);
        var requestedAgent = interactionRequest.Agent ?? interactionRequest.Model ?? string.Empty;
        var ownership = new GoogleToolExecutionOwnership(request);
        var interaction = await GetInteraction(interactionRequest, cancellationToken);
        var mappedResponse = interaction.ToUnifiedResponse(GetIdentifier());
        var clientResponse = ownership.Apply(mappedResponse);

        return AddAntigravityStateTool(
            clientResponse,
            interaction, requestedAgent, isCustomAgent);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var interactionRequest = CreateGoogleUnifiedInteractionRequest(request);
        var isCustomAgent = IsCustomAgentSelection(interactionRequest);
        var requestedAgent = interactionRequest.Agent ?? interactionRequest.Model ?? string.Empty;
        interactionRequest.Stream = true;
        this.SetDefaultInteractionProperties(interactionRequest);
        var ownership = new GoogleToolExecutionOwnership(request);
        var clientArguments = new GoogleClientToolArgumentStream(ownership);

        string? interactionId = null;
        string? environmentId = null;

        await foreach (var update in GetInteractions(
                           interactionRequest,
                           cancellationToken: cancellationToken))
        {
            if (update is InteractionCreatedEvent { Interaction: not null } created)
            {
                interactionId = created.Interaction.Id ?? interactionId;
                environmentId = ExtractGoogleAgentEnvironmentId(created.Interaction) ?? environmentId;
            }

            if (update is InteractionCompletedEvent { Interaction: not null } completed)
            {
                interactionId = completed.Interaction.Id ?? interactionId;
                environmentId = ExtractGoogleAgentEnvironmentId(completed.Interaction) ?? environmentId;

                if (!string.IsNullOrWhiteSpace(interactionId)
                    && (isCustomAgent || IsAntigravityAgent(requestedAgent))
                    && (isCustomAgent || !string.IsNullOrWhiteSpace(environmentId)))
                {
                    var state = new AntigravityContinuationState(
                        interactionId,
                        environmentId,
                        NormalizeContinuationAgent(requestedAgent));

                    foreach (var stateEvent in CreateAntigravityStateToolEvents(
                                 state,
                                 completed.Interaction,
                                  DateTimeOffset.UtcNow,
                                  isCustomAgent))
                    {
                        yield return stateEvent;
                    }
                }
            }

            foreach (var mappingUpdate in clientArguments.ForMapping(update))
            foreach (var streamEvent in mappingUpdate.ToUnifiedStreamEvent(GetIdentifier()))
            {
                var clientEvent = MarkGoogleAgentUnifiedToolEventProviderExecuted(streamEvent, ownership, mappingUpdate);
                yield return clientEvent;
            }
        }
    }
}
