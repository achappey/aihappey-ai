using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    // Ownership is scoped to this request. Provider options and tools configured on
    // the agent are not evidence that the caller can execute a tool.
    private sealed class GoogleToolExecutionOwnership(AIRequest request)
    {
        private readonly HashSet<string> clientToolNames = GetClientToolNames(request);
        private readonly Dictionary<string, bool> calls = new(StringComparer.Ordinal);

        private static HashSet<string> GetClientToolNames(AIRequest request)
        {
            // Use the canonical mapper to respect native/raw tool definitions, but
            // deliberately omit provider metadata so its tools cannot become clients.
            var tools = new AIRequest { ProviderId = request.ProviderId, Tools = request.Tools }
                .ToInteractionRequest(GoogleExtensions.Identifier()).Tools;
            return new HashSet<string>((tools ?? []).OfType<InteractionFunctionTool>()
                .Select(tool => tool.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name) && !IsReservedTool(name))
                .Select(name => name!), StringComparer.Ordinal);
        }

        private static bool IsReservedTool(string? name)
            => name is AntigravityStateToolName or CustomAgentStateToolName or GoogleAgentDownloadFileToolName;

        public bool IsClientTool(string? name) => name is not null && clientToolNames.Contains(name);

        private bool Classify(string? callId, string? toolName, bool isFunction, bool isNative)
        {
            // Native event types and synthetic transport tools always win, even if
            // the request contains a client function with the same name.
            var providerExecuted = true;
            if (!isNative && !IsReservedTool(toolName))
            {
                if (!string.IsNullOrWhiteSpace(callId) && calls.TryGetValue(callId, out var remembered))
                    providerExecuted = remembered;
                else if (isFunction && toolName is not null && clientToolNames.Contains(toolName))
                    providerExecuted = false;
            }
            if (!string.IsNullOrWhiteSpace(callId))
                calls[callId] = providerExecuted;
            return providerExecuted;
        }

        public bool IsProviderExecuted(AIStreamEvent streamEvent, InteractionStreamEventPart? sourceEvent)
        {
            var data = streamEvent.Event.Data;
            if (data is not (AIToolInputStartEventData or AIToolInputAvailableEventData
                or AIToolOutputAvailableEventData or AIToolOutputErrorEventData))
                return true;

            var toolName = data switch
            {
                AIToolInputStartEventData start => start.ToolName,
                AIToolInputAvailableEventData input => input.ToolName,
                AIToolOutputAvailableEventData output => output.ToolName,
                _ => null
            };
            var callId = data is AIToolOutputErrorEventData error
                ? error.ToolCallId ?? streamEvent.Event.Id : streamEvent.Event.Id;
            var content = sourceEvent is InteractionStepStartEvent startEvent
                ? startEvent.Step is InteractionModelOutputStep modelOutput
                    ? modelOutput.Content?.FirstOrDefault() : startEvent.Step
                : null;
            var type = sourceEvent is InteractionStepDeltaEvent delta ? delta.Delta?.Type : content?.Type;
            var isFunction = type is "function_call" or "function_result" or "arguments_delta";
            var isNative = type is not null && !isFunction;
            return Classify(callId, toolName, isFunction, isNative);
        }

        public AIResponse Apply(AIResponse response)
        {
            // Register calls first, including calls inside model_output steps, so
            // unnamed results can be resolved regardless of output-item ordering.
            var parts = (response.Output?.Items ?? []).SelectMany(item => item.Content ?? []).OfType<AIToolCallContentPart>();
            foreach (var part in parts.Where(part => part.Type != "function_result"))
                Classify(part.ToolCallId, part.ToolName, part.Type == "function_call", part.Type != "function_call");

            return new AIResponse
            {
                ProviderId = response.ProviderId, Model = response.Model, Status = response.Status,
                Usage = response.Usage, Metadata = response.Metadata,
                Output = response.Output is null ? null : new AIOutput
                {
                    Metadata = response.Output.Metadata,
                    Items = response.Output.Items?.Select(item => new AIOutputItem
                    {
                        Type = item.Type, Role = item.Role, Metadata = item.Metadata,
                        Content = item.Content?.Select(part => part is AIToolCallContentPart tool ? Apply(tool) : part).ToList()
                    }).ToList()
                }
            };
        }

        private AIToolCallContentPart Apply(AIToolCallContentPart tool) => new()
        {
            Type = tool.Type, ToolCallId = tool.ToolCallId, ToolName = tool.ToolName, Title = tool.Title,
            Input = tool.Input, Output = tool.Output, State = tool.State, Approval = tool.Approval, Metadata = tool.Metadata,
            ProviderExecuted = Classify(tool.ToolCallId, tool.ToolName,
                tool.Type is "function_call" or "function_result", tool.Type is not ("function_call" or "function_result"))
        };
    }

    // Google sends initial arguments in step.start and streams their JSON again in
    // arguments_delta (confirmed by runtime trace call_2442063). The shared mapper
    // treats initial arguments as a prefix. Adapt only client function calls to a
    // delta-only stream; retain the initial arguments as the no-delta fallback.
    // This does not repair malformed JSON or remove repeated argument fragments.
    private sealed class GoogleClientToolArgumentStream(GoogleToolExecutionOwnership ownership)
    {
        private sealed record Call(object? InitialArguments, bool HasDelta = false);
        private readonly Dictionary<int, Call> calls = [];

        public IEnumerable<InteractionStreamEventPart> ForMapping(InteractionStreamEventPart update)
        {
            if (update is InteractionCreatedEvent)
                calls.Clear();

            if (update is InteractionStepStartEvent start)
            {
                var content = start.Step is InteractionModelOutputStep model
                    ? model.Content?.FirstOrDefault() : start.Step;
                if (content is InteractionFunctionCallContent call && ownership.IsClientTool(call.Name))
                {
                    calls[start.Index] = new Call(call.Arguments);
                    var mappedCall = new InteractionFunctionCallContent
                    {
                        Id = call.Id, Name = call.Name, Signature = call.Signature,
                        Type = call.Type, AdditionalProperties = call.AdditionalProperties,
                        Arguments = null
                    };
                    InteractionStep step = mappedCall;
                    if (start.Step is InteractionModelOutputStep modelOutput)
                        step = new InteractionModelOutputStep
                        {
                            Type = modelOutput.Type,
                            AdditionalProperties = modelOutput.AdditionalProperties,
                            Content = [mappedCall, .. (modelOutput.Content ?? []).Skip(1)]
                        };
                    yield return new InteractionStepStartEvent
                    {
                        EventType = start.EventType, EventId = start.EventId,
                        Index = start.Index, AdditionalProperties = start.AdditionalProperties, Step = step
                    };
                    yield break;
                }
            }
            else if (update is InteractionStepDeltaEvent { Delta.Type: "arguments_delta" } delta
                     && calls.TryGetValue(delta.Index, out var active))
            {
                var arguments = delta.Delta.AdditionalProperties?.GetValueOrDefault("arguments");
                var text = arguments is { ValueKind: JsonValueKind.String } value ? value.GetString()
                    : arguments is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } json ? json.ToString()
                    : delta.Delta.Text;
                if (!string.IsNullOrWhiteSpace(text))
                    calls[delta.Index] = active with { HasDelta = true };
            }
            else if (update is InteractionStepStopEvent stop && calls.Remove(stop.Index, out var stopped))
            {
                if (!stopped.HasDelta && stopped.InitialArguments is not null)
                {
                    var text = stopped.InitialArguments switch
                    {
                        string value => value,
                        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
                        _ => JsonSerializer.Serialize(stopped.InitialArguments, GoogleAgentJsonOptions)
                    };
                    yield return new InteractionStepDeltaEvent
                    {
                        Index = stop.Index, EventId = stop.EventId,
                        Delta = new InteractionContentDeltaData
                        {
                            Type = "arguments_delta",
                            AdditionalProperties = new() { ["arguments"] = JsonSerializer.SerializeToElement(text, GoogleAgentJsonOptions) }
                        }
                    };
                }
            }

            yield return update;
        }
    }
}
