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

        var interaction = await GetInteraction(
            CreateGoogleUnifiedInteractionRequest(request),
            cancellationToken);

        return AddAntigravityStateTool(
            interaction.ToUnifiedResponse(GetIdentifier()),
            interaction);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var interactionRequest = CreateGoogleUnifiedInteractionRequest(request);
        interactionRequest.Stream = true;
        this.SetDefaultInteractionProperties(interactionRequest);

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

                if (IsAntigravityAgent(completed.Interaction.Agent ?? completed.Interaction.Model)
                    && !string.IsNullOrWhiteSpace(interactionId)
                    && !string.IsNullOrWhiteSpace(environmentId))
                {
                    var state = new AntigravityContinuationState(
                        interactionId,
                        environmentId,
                        NormalizeGoogleModelOrAgentId(completed.Interaction.Agent ?? completed.Interaction.Model));

                    foreach (var stateEvent in CreateAntigravityStateToolEvents(
                                 state,
                                 completed.Interaction,
                                 DateTimeOffset.UtcNow))
                    {
                        yield return stateEvent;
                    }
                }
            }

            foreach (var streamEvent in update.ToUnifiedStreamEvent(GetIdentifier()))
                yield return MarkGoogleAgentUnifiedToolEventProviderExecuted(streamEvent);
        }
    }
}
