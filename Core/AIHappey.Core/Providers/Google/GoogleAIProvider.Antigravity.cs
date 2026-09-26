using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private const string AntigravityStateToolName = "google_antigravity_state";
    private const string AntigravityStateToolTitle = "Google Antigravity interaction state";
    private const string CustomAgentStateToolName = "google_custom_agent_state";
    private const string CustomAgentStateToolTitle = "Google custom agent interaction state";

    private sealed record AntigravityContinuationState(
        string InteractionId,
        string? EnvironmentId,
        string? Agent);

    private static bool IsCustomAgentSelection(InteractionRequest request)
        => NormalizeGoogleModelOrAgentId(request.Agent ?? request.Model)
            .StartsWith(CustomAgentIdPrefix, StringComparison.OrdinalIgnoreCase)
           || !string.IsNullOrWhiteSpace(request.Agent)
              && !IsDeepResearchAgent(request.Agent)
              && !IsAntigravityAgent(request.Agent);

    private static string NormalizeContinuationAgent(string? agent)
    {
        var normalized = NormalizeGoogleModelOrAgentId(agent);
        return normalized.StartsWith(CustomAgentIdPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[CustomAgentIdPrefix.Length..]
            : normalized;
    }

    private InteractionRequest CreateGoogleUnifiedInteractionRequest(AIRequest request)
    {
        var interactionRequest = request.ToInteractionRequest(GetIdentifier());
        var requestedAgent = NormalizeGoogleModelOrAgentId(interactionRequest.Agent ?? interactionRequest.Model);

        var isCustomAgent = IsCustomAgentSelection(interactionRequest);
        if ((!isCustomAgent && !IsAntigravityAgent(requestedAgent))
            || !TryFindAntigravityContinuationState(request, requestedAgent, isCustomAgent, out var state, out var stateItemIndex))
        {
            return interactionRequest;
        }

        var continuationRequest = CloneUnifiedRequestWithInputAfterState(request, stateItemIndex);
        interactionRequest = continuationRequest.ToInteractionRequest(GetIdentifier());
        interactionRequest.PreviousInteractionId = state.InteractionId;
        if (!string.IsNullOrWhiteSpace(state.EnvironmentId))
            (interactionRequest.AdditionalProperties ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase))
                [GoogleAgentEnvironmentPropertyName] = JsonSerializer.SerializeToElement(state.EnvironmentId, GoogleAgentJsonOptions);

        return interactionRequest;
    }

    private static AIRequest CloneUnifiedRequestWithInputAfterState(AIRequest request, int stateItemIndex)
    {
        var items = request.Input?.Items ?? [];
        var continuationItems = stateItemIndex + 1 < items.Count
            ? items.Skip(stateItemIndex + 1).ToList()
            : [];

        return new AIRequest
        {
            ProviderId = request.ProviderId,
            Model = request.Model,
            Id = request.Id,
            Instructions = request.Instructions,
            Input = new AIInput
            {
                Text = null,
                Items = continuationItems,
                Metadata = request.Input?.Metadata
            },
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxOutputTokens,
            MaxToolCalls = request.MaxToolCalls,
            Stream = request.Stream,
            ParallelToolCalls = request.ParallelToolCalls,
            ToolChoice = request.ToolChoice,
            ResponseFormat = request.ResponseFormat,
            Tools = request.Tools,
            Metadata = request.Metadata,
            Headers = request.Headers,
            Verbosity = request.Verbosity
        };
    }

    private static bool TryFindAntigravityContinuationState(
        AIRequest request,
        string requestedAgent,
        bool isCustomAgent,
        out AntigravityContinuationState state,
        out int stateItemIndex)
    {
        var items = request.Input?.Items ?? [];

        for (var itemIndex = items.Count - 1; itemIndex >= 0; itemIndex--)
        {
            var parts = items[itemIndex].Content ?? [];
            for (var partIndex = parts.Count - 1; partIndex >= 0; partIndex--)
            {
                if (parts[partIndex] is not AIToolCallContentPart tool
                    || tool.ProviderExecuted != true
                    || !IsAntigravityStateToolPart(tool, isCustomAgent))
                {
                    continue;
                }

                if (!TryExtractAntigravityContinuationState(tool.Output, out state)
                    && !TryExtractAntigravityContinuationState(tool.Metadata, out state)
                    && !TryExtractAntigravityContinuationState(tool.Input, out state))
                {
                    continue;
                }

                if (!IsMatchingContinuationState(state, requestedAgent, isCustomAgent))
                {
                    continue;
                }

                stateItemIndex = itemIndex;
                return true;
            }

            // Strict Responses replays the reserved transport state as a completed
            // function pair. The output item follows the call item, so use it as the
            // state marker and recover the reserved call identity by call id.
            foreach (var outputPart in parts.OfType<AIToolCallContentPart>())
            {
                if (!string.Equals(outputPart.Type, "function_call_output", StringComparison.OrdinalIgnoreCase)
                    || !TryFindReservedAntigravityCall(items, itemIndex, outputPart.ToolCallId, isCustomAgent, out _))
                {
                    continue;
                }

                if (!TryExtractAntigravityContinuationState(outputPart.Output, out state))
                    continue;

                if (!IsMatchingContinuationState(state, requestedAgent, isCustomAgent))
                {
                    continue;
                }

                stateItemIndex = itemIndex;
                return true;
            }
        }

        state = default!;
        stateItemIndex = -1;
        return false;
    }

    private static bool IsMatchingContinuationState(AntigravityContinuationState state, string requestedAgent, bool isCustomAgent)
        => !string.IsNullOrWhiteSpace(state.Agent)
           && string.Equals(NormalizeContinuationAgent(state.Agent), NormalizeContinuationAgent(requestedAgent), StringComparison.OrdinalIgnoreCase)
           && (isCustomAgent || !string.IsNullOrWhiteSpace(state.EnvironmentId));

    private static bool TryFindReservedAntigravityCall(
        IReadOnlyList<AIInputItem> items,
        int outputItemIndex,
        string? callId,
        bool isCustomAgent,
        out int callItemIndex)
    {
        callItemIndex = -1;
        if (string.IsNullOrWhiteSpace(callId))
            return false;

        for (var index = outputItemIndex - 1; index >= 0; index--)
        {
            var call = (items[index].Content ?? [])
                .OfType<AIToolCallContentPart>()
                .FirstOrDefault(part =>
                    string.Equals(part.Type, "function_call", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(part.ToolCallId, callId, StringComparison.Ordinal)
                    && string.Equals(part.ToolName, isCustomAgent ? CustomAgentStateToolName : AntigravityStateToolName, StringComparison.OrdinalIgnoreCase));
            if (call is null)
                continue;

            callItemIndex = index;
            return true;
        }

        return false;
    }

    private static bool IsAntigravityStateToolPart(AIToolCallContentPart tool, bool isCustomAgent)
    {
        var toolName = isCustomAgent ? CustomAgentStateToolName : AntigravityStateToolName;
        var title = isCustomAgent ? CustomAgentStateToolTitle : AntigravityStateToolTitle;
        if (string.Equals(tool.ToolName, toolName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool.ToolName, $"tool-{toolName}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool.Title, title, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool.Type, $"tool-{toolName}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
                   tool.Metadata?.GetValueOrDefault("type")?.ToString(),
                   toolName,
                   StringComparison.OrdinalIgnoreCase)
               || string.Equals(
                   tool.Metadata?.GetValueOrDefault("tool_name")?.ToString(),
                   toolName,
                   StringComparison.OrdinalIgnoreCase)
               || string.Equals(
                   tool.Metadata?.GetValueOrDefault("messages.block.type")?.ToString(),
                   toolName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExtractAntigravityContinuationState(
        object? value,
        out AntigravityContinuationState state)
    {
        state = default!;
        if (value is null)
            return false;

        JsonElement element;
        try
        {
            element = value is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(value, GoogleAgentJsonOptions);
        }
        catch
        {
            return false;
        }

        return TryExtractAntigravityContinuationState(element, out state);
    }

    private static bool TryExtractAntigravityContinuationState(
        JsonElement element,
        out AntigravityContinuationState state)
    {
        state = default!;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var containerName in new[] { "structuredContent", "structured_content", "output", "state", "interaction", GoogleExtensions.Identifier() })
        {
            if (TryGetJsonProperty(element, containerName, out var nested)
                && nested.ValueKind == JsonValueKind.Object
                && TryExtractAntigravityContinuationState(nested, out state))
            {
                return true;
            }
        }

        var interactionId = TryGetJsonString(element, "interactionId")
                            ?? TryGetJsonString(element, "interaction_id")
                            ?? TryGetJsonString(element, "previousInteractionId")
                            ?? TryGetJsonString(element, "previous_interaction_id");
        var environmentId = TryGetJsonString(element, "environmentId")
                            ?? TryGetJsonString(element, "environment_id");

        if (string.IsNullOrWhiteSpace(interactionId))
            return false;

        state = new AntigravityContinuationState(
            interactionId,
            environmentId,
            TryGetJsonString(element, "agent") ?? TryGetJsonString(element, "agentId") ?? TryGetJsonString(element, "agent_id"));
        return true;
    }

    private static bool TryGetJsonProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? TryGetJsonString(JsonElement element, string name)
    {
        if (!TryGetJsonProperty(element, name, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static AIResponse AddAntigravityStateTool(AIResponse response, Interaction interaction, string requestedAgent, bool isCustomAgent)
    {
        if (!TryCreateAntigravityContinuationState(interaction, requestedAgent, isCustomAgent, out var state))
            return response;

        var items = response.Output?.Items?.ToList() ?? [];
        items.Add(CreateAntigravityStateOutputItem(state, interaction, isCustomAgent));

        return new AIResponse
        {
            ProviderId = response.ProviderId,
            Model = response.Model,
            Status = response.Status,
            Output = new AIOutput
            {
                Items = items,
                Metadata = response.Output?.Metadata
            },
            Usage = response.Usage,
            Metadata = response.Metadata
        };
    }

    private static bool TryCreateAntigravityContinuationState(
        Interaction interaction,
        string requestedAgent,
        bool isCustomAgent,
        out AntigravityContinuationState state)
    {
        var interactionId = interaction.Id;
        var environmentId = ExtractGoogleAgentEnvironmentId(interaction);
        var agent = NormalizeContinuationAgent(requestedAgent);

        if (string.IsNullOrWhiteSpace(interactionId)
            || string.IsNullOrWhiteSpace(agent)
            || !isCustomAgent && (!IsAntigravityAgent(agent) || string.IsNullOrWhiteSpace(environmentId)))
        {
            state = default!;
            return false;
        }

        state = new AntigravityContinuationState(interactionId, environmentId, agent);
        return true;
    }

    private static AIOutputItem CreateAntigravityStateOutputItem(
        AntigravityContinuationState state,
        Interaction interaction,
        bool isCustomAgent)
        => new()
        {
            Type = "message",
            Role = "assistant",
            Content =
            [
                new AIToolCallContentPart
                {
                    Type = "tool-call",
                    ToolCallId = BuildAntigravityStateToolCallId(state.InteractionId, isCustomAgent),
                    ToolName = isCustomAgent ? CustomAgentStateToolName : AntigravityStateToolName,
                    Title = isCustomAgent ? CustomAgentStateToolTitle : AntigravityStateToolTitle,
                    Input = CreateAntigravityStateToolInput(state),
                    Output = CreateAntigravityStateToolResult(state, interaction, isCustomAgent),
                    ProviderExecuted = true,
                    State = "output-available",
                    Metadata = CreateAntigravityStateToolMetadata(state, isCustomAgent)
                }
            ]
        };

    private static IEnumerable<AIStreamEvent> CreateAntigravityStateToolEvents(
        AntigravityContinuationState state,
        Interaction interaction,
        DateTimeOffset timestamp,
        bool isCustomAgent)
    {
        var toolCallId = BuildAntigravityStateToolCallId(state.InteractionId, isCustomAgent);
        var toolName = isCustomAgent ? CustomAgentStateToolName : AntigravityStateToolName;
        var providerMetadata = CreateGoogleAgentProviderExecutedToolProviderMetadata(toolName);

        yield return CreateAntigravityStateStreamEvent(
            "tool-input-available",
            toolCallId,
            timestamp,
            new AIToolInputAvailableEventData
            {
                ToolName = toolName,
                Title = isCustomAgent ? CustomAgentStateToolTitle : AntigravityStateToolTitle,
                Input = CreateAntigravityStateToolInput(state),
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            });

        yield return CreateAntigravityStateStreamEvent(
            "tool-output-available",
            toolCallId,
            timestamp,
            new AIToolOutputAvailableEventData
            {
                ToolName = toolName,
                Output = CreateAntigravityStateToolResult(state, interaction, isCustomAgent),
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            });
    }

    private static AIStreamEvent CreateAntigravityStateStreamEvent(
        string type,
        string id,
        DateTimeOffset timestamp,
        object data)
        => new()
        {
            ProviderId = GoogleExtensions.Identifier(),
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = timestamp,
                Data = data
            }
        };

    private static object CreateAntigravityStateToolInput(AntigravityContinuationState state)
        => new
        {
            agent = state.Agent,
            environment_id = state.EnvironmentId
        };

    private static CallToolResult CreateAntigravityStateToolResult(
        AntigravityContinuationState state,
        Interaction interaction,
        bool isCustomAgent)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                type = isCustomAgent ? CustomAgentStateToolName : AntigravityStateToolName,
                interactionId = state.InteractionId,
                interaction_id = state.InteractionId,
                environmentId = state.EnvironmentId,
                environment_id = state.EnvironmentId,
                agent = state.Agent,
                interaction
            }, GoogleAgentJsonOptions)
        };

    private static Dictionary<string, object?> CreateAntigravityStateToolMetadata(AntigravityContinuationState state, bool isCustomAgent)
        => new()
        {
            ["type"] = isCustomAgent ? CustomAgentStateToolName : AntigravityStateToolName,
            ["tool_name"] = isCustomAgent ? CustomAgentStateToolName : AntigravityStateToolName,
            ["interactionId"] = state.InteractionId,
            ["interaction_id"] = state.InteractionId,
            ["environmentId"] = state.EnvironmentId,
            ["environment_id"] = state.EnvironmentId,
            ["agent"] = state.Agent,
            [GoogleExtensions.Identifier()] = JsonSerializer.SerializeToElement(new
            {
                type = isCustomAgent ? CustomAgentStateToolName : AntigravityStateToolName,
                tool_name = isCustomAgent ? CustomAgentStateToolName : AntigravityStateToolName,
                interaction_id = state.InteractionId,
                environment_id = state.EnvironmentId,
                agent = state.Agent
            }, GoogleAgentJsonOptions)
        };

    private static string BuildAntigravityStateToolCallId(string interactionId, bool isCustomAgent)
        => isCustomAgent ? $"google-custom-agent-state-{interactionId}" : $"google-antigravity-state-{interactionId}";
}
