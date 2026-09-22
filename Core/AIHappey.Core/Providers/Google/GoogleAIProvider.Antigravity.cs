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

    private sealed record AntigravityContinuationState(
        string InteractionId,
        string EnvironmentId,
        string? Agent);

    private InteractionRequest CreateGoogleUnifiedInteractionRequest(AIRequest request)
    {
        var interactionRequest = request.ToInteractionRequest(GetIdentifier());
        var requestedAgent = NormalizeGoogleModelOrAgentId(interactionRequest.Agent ?? interactionRequest.Model);

        if (!IsAntigravityAgent(requestedAgent)
            || !TryFindAntigravityContinuationState(request, requestedAgent, out var state, out var stateItemIndex))
        {
            return interactionRequest;
        }

        var continuationRequest = CloneUnifiedRequestWithInputAfterState(request, stateItemIndex);
        interactionRequest = continuationRequest.ToInteractionRequest(GetIdentifier());
        interactionRequest.PreviousInteractionId = state.InteractionId;
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
                    || !IsAntigravityStateToolPart(tool))
                {
                    continue;
                }

                if (!TryExtractAntigravityContinuationState(tool.Output, out state)
                    && !TryExtractAntigravityContinuationState(tool.Metadata, out state)
                    && !TryExtractAntigravityContinuationState(tool.Input, out state))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(state.Agent)
                    && !string.Equals(
                        NormalizeGoogleModelOrAgentId(state.Agent),
                        requestedAgent,
                        StringComparison.OrdinalIgnoreCase))
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

    private static bool IsAntigravityStateToolPart(AIToolCallContentPart tool)
    {
        if (string.Equals(tool.ToolName, AntigravityStateToolName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool.ToolName, $"tool-{AntigravityStateToolName}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool.Title, AntigravityStateToolTitle, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool.Type, $"tool-{AntigravityStateToolName}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
                   tool.Metadata?.GetValueOrDefault("type")?.ToString(),
                   AntigravityStateToolName,
                   StringComparison.OrdinalIgnoreCase)
               || string.Equals(
                   tool.Metadata?.GetValueOrDefault("tool_name")?.ToString(),
                   AntigravityStateToolName,
                   StringComparison.OrdinalIgnoreCase)
               || string.Equals(
                   tool.Metadata?.GetValueOrDefault("messages.block.type")?.ToString(),
                   AntigravityStateToolName,
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

        if (string.IsNullOrWhiteSpace(interactionId) || string.IsNullOrWhiteSpace(environmentId))
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

    private static AIResponse AddAntigravityStateTool(AIResponse response, Interaction interaction)
    {
        if (!TryCreateAntigravityContinuationState(interaction, out var state))
            return response;

        var items = response.Output?.Items?.ToList() ?? [];
        items.Add(CreateAntigravityStateOutputItem(state, interaction));

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
        out AntigravityContinuationState state)
    {
        var interactionId = interaction.Id;
        var environmentId = ExtractGoogleAgentEnvironmentId(interaction);
        var agent = NormalizeGoogleModelOrAgentId(interaction.Agent ?? interaction.Model);

        if (!IsAntigravityAgent(agent)
            || string.IsNullOrWhiteSpace(interactionId)
            || string.IsNullOrWhiteSpace(environmentId))
        {
            state = default!;
            return false;
        }

        state = new AntigravityContinuationState(interactionId, environmentId, agent);
        return true;
    }

    private static AIOutputItem CreateAntigravityStateOutputItem(
        AntigravityContinuationState state,
        Interaction interaction)
        => new()
        {
            Type = "message",
            Role = "assistant",
            Content =
            [
                new AIToolCallContentPart
                {
                    Type = "tool-call",
                    ToolCallId = BuildAntigravityStateToolCallId(state.InteractionId),
                    ToolName = AntigravityStateToolName,
                    Title = AntigravityStateToolTitle,
                    Input = CreateAntigravityStateToolInput(state),
                    Output = CreateAntigravityStateToolResult(state, interaction),
                    ProviderExecuted = true,
                    State = "output-available",
                    Metadata = CreateAntigravityStateToolMetadata(state)
                }
            ]
        };

    private static IEnumerable<AIStreamEvent> CreateAntigravityStateToolEvents(
        AntigravityContinuationState state,
        Interaction interaction,
        DateTimeOffset timestamp)
    {
        var toolCallId = BuildAntigravityStateToolCallId(state.InteractionId);
        var providerMetadata = CreateGoogleAgentProviderExecutedToolProviderMetadata(AntigravityStateToolName);

        yield return CreateAntigravityStateStreamEvent(
            "tool-input-available",
            toolCallId,
            timestamp,
            new AIToolInputAvailableEventData
            {
                ToolName = AntigravityStateToolName,
                Title = AntigravityStateToolTitle,
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
                ToolName = AntigravityStateToolName,
                Output = CreateAntigravityStateToolResult(state, interaction),
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
        Interaction interaction)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                type = AntigravityStateToolName,
                interactionId = state.InteractionId,
                interaction_id = state.InteractionId,
                environmentId = state.EnvironmentId,
                environment_id = state.EnvironmentId,
                agent = state.Agent,
                interaction
            }, GoogleAgentJsonOptions)
        };

    private static Dictionary<string, object?> CreateAntigravityStateToolMetadata(AntigravityContinuationState state)
        => new()
        {
            ["type"] = AntigravityStateToolName,
            ["tool_name"] = AntigravityStateToolName,
            ["interactionId"] = state.InteractionId,
            ["interaction_id"] = state.InteractionId,
            ["environmentId"] = state.EnvironmentId,
            ["environment_id"] = state.EnvironmentId,
            ["agent"] = state.Agent,
            [GoogleExtensions.Identifier()] = JsonSerializer.SerializeToElement(new
            {
                type = AntigravityStateToolName,
                tool_name = AntigravityStateToolName,
                interaction_id = state.InteractionId,
                environment_id = state.EnvironmentId,
                agent = state.Agent
            }, GoogleAgentJsonOptions)
        };

    private static string BuildAntigravityStateToolCallId(string interactionId)
        => $"google-antigravity-state-{interactionId}";
}
