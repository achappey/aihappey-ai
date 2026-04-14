using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model.Providers.Cohere;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Cohere;

public partial class CohereProvider
{
    private static readonly JsonSerializerOptions CohereJsonSerializerOptions = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var providerMetadata = GetUnifiedProviderMetadata(request);
        var providerMetadataNode = GetUnifiedProviderMetadataNode(request);
        var payload = BuildUnifiedRequestPayload(request, providerMetadata, providerMetadataNode, stream: false);

        using var httpRequest = CreateUnifiedHttpRequest(payload, stream: false, request.Headers);
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        EnsureUnifiedSuccess(response, responseBody);

        using var document = JsonDocument.Parse(responseBody);
        return CreateUnifiedResponse(request, payload, document.RootElement.Clone());
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var providerId = GetIdentifier();
        var providerMetadata = GetUnifiedProviderMetadata(request);
        var providerMetadataNode = GetUnifiedProviderMetadataNode(request);
        var payload = BuildUnifiedRequestPayload(request, providerMetadata, providerMetadataNode, stream: true);

        if (request.Input?.Items is { Count: > 0 } inputItems)
        {
            yield return CreateDataStreamEvent(
                providerId,
                "data-cohere.replay.input-items",
                JsonSerializer.SerializeToElement(inputItems, CohereJsonSerializerOptions),
                request.Headers);
        }

        yield return CreateDataStreamEvent(
            providerId,
            "data-cohere.replay.messages",
            payload["messages"]?.DeepClone() ?? new JsonArray(),
            request.Headers);

        yield return CreateDataStreamEvent(
            providerId,
            "data-cohere.request",
            JsonSerializer.SerializeToElement(payload, CohereJsonSerializerOptions),
            request.Headers);

        using var httpRequest = CreateUnifiedHttpRequest(payload, stream: true, request.Headers);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var responseId = request.Id ?? Guid.NewGuid().ToString("n");
        var activeModel = request.Model;
        var finishEmitted = false;
        var lastTimestamp = DateTimeOffset.UtcNow;
        var responseMetadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["cohere.requested_model"] = request.Model,
            ["cohere.stream"] = true
        };

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorMessage = BuildUnifiedErrorMessage(response, errorText);

            yield return CreateStreamEvent(
                providerId,
                responseId,
                "error",
                new AIErrorEventData { ErrorText = errorMessage },
                lastTimestamp,
                new Dictionary<string, object?>(responseMetadata)
                {
                    ["cohere.http.status"] = (int)response.StatusCode,
                    ["cohere.http.reason"] = response.ReasonPhrase,
                    ["cohere.error.raw"] = string.IsNullOrWhiteSpace(errorText) ? null : errorText
                });

            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var activeContents = new Dictionary<int, CohereStreamingContentState>();
        var activeToolsByIndex = new Dictionary<int, CohereStreamingToolState>();
        var activeToolsById = new Dictionary<string, CohereStreamingToolState>(StringComparer.Ordinal);
        var activeToolExecutionOrder = new List<string>();

        var toolPlanState = new CohereStreamingContentState
        {
            Index = -1,
            EventId = $"{responseId}:tool-plan",
            Kind = "thinking"
        };

        var sseDataLines = new List<string>();
        string? sseEventName = null;
        string? line;

        while (!cancellationToken.IsCancellationRequested
               && (line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (line.Length == 0)
            {
                foreach (var streamEvent in ProcessSseEvent())
                    yield return streamEvent;

                sseDataLines.Clear();
                sseEventName = null;
                continue;
            }

            if (line.StartsWith(':'))
                continue;

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                sseEventName = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                sseDataLines.Add(line["data:".Length..].TrimStart());
            }
        }

        foreach (var streamEvent in ProcessSseEvent())
            yield return streamEvent;

        if (!finishEmitted)
        {
            foreach (var pending in FinalizePendingContent(lastTimestamp))
                yield return pending;

            foreach (var pendingTool in FinalizePendingToolCalls(lastTimestamp))
                yield return pendingTool;

            yield return CreateFinishStreamEvent(
                providerId,
                responseId,
                lastTimestamp,
                activeModel,
                finishReason: "stop",
                inputTokens: null,
                outputTokens: null,
                totalTokens: null,
                metadata: responseMetadata);
        }

        IEnumerable<AIStreamEvent> ProcessSseEvent()
        {
            if (sseDataLines.Count == 0)
                yield break;

            var combinedData = string.Join("\n", sseDataLines);
            if (string.IsNullOrWhiteSpace(combinedData))
                yield break;

            if (string.Equals(combinedData, "[DONE]", StringComparison.OrdinalIgnoreCase))
                yield break;

            JsonDocument? rawDocument = null;
            JsonElement rawElement;

            rawDocument = JsonDocument.Parse(combinedData);
            rawElement = rawDocument.RootElement.Clone();

            using (rawDocument)
            {
                lastTimestamp = DateTimeOffset.UtcNow;

                var unwrapped = UnwrapJsonEnvelope(rawElement);
                var eventName = ResolveStreamEventName(unwrapped, sseEventName);
                var payloadElement = ResolveStreamPayload(unwrapped);

                if (payloadElement.TryGetProperty("id", out var idElement)
                    && idElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(idElement.GetString()))
                {
                    responseId = idElement.GetString()!;
                    responseMetadata["cohere.response.id"] = responseId;
                }

                if (!string.IsNullOrWhiteSpace(activeModel))
                    responseMetadata["cohere.model"] = activeModel;

                yield return CreateDataStreamEvent(
                    providerId,
                    "data-cohere.chunk",
                    unwrapped,
                    metadata: new Dictionary<string, object?>(responseMetadata)
                    {
                        ["cohere.event.type"] = eventName,
                        ["cohere.event.header"] = sseEventName
                    });

                if (string.IsNullOrWhiteSpace(eventName))
                    yield break;

                switch (eventName)
                {
                    case "message-start":
                        {
                            if (payloadElement.TryGetProperty("id", out var messageId)
                                && messageId.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(messageId.GetString()))
                            {
                                responseId = messageId.GetString()!;
                                responseMetadata["cohere.response.id"] = responseId;
                            }

                            break;
                        }

                    case "content-start":
                    case "content-delta":
                        {
                            var index = GetInt32(payloadElement, "index") ?? 0;
                            var contentNode = GetProperty(payloadElement, "delta") is { } delta
                                && GetProperty(delta, "message") is { } deltaMessage
                                && GetProperty(deltaMessage, "content") is { } nestedContent
                                    ? nestedContent
                                    : GetProperty(payloadElement, "message") is { } message
                                      && GetProperty(message, "content") is { } messageContent
                                        ? messageContent
                                        : payloadElement;

                            var kind = DetermineContentKind(contentNode, activeContents.TryGetValue(index, out var existingContentState) ? existingContentState.Kind : null);
                            if (string.IsNullOrWhiteSpace(kind))
                                break;

                            var contentState = existingContentState ?? new CohereStreamingContentState
                            {
                                Index = index,
                                Kind = kind,
                                EventId = $"{responseId}:{kind}:{index}"
                            };

                            contentState.Kind = kind;
                            activeContents[index] = contentState;

                            if (!contentState.StartEmitted)
                            {
                                yield return kind == "thinking"
                                    ? CreateStreamEvent(
                                        providerId,
                                        contentState.EventId,
                                        "reasoning-start",
                                        new AIReasoningStartEventData
                                        {
                                            ProviderMetadata = CreateScopedProviderMetadata(providerId, new Dictionary<string, object>
                                            {
                                                ["type"] = kind,
                                                ["content_index"] = index
                                            })
                                        },
                                        lastTimestamp,
                                        responseMetadata)
                                    : CreateStreamEvent(
                                        providerId,
                                        contentState.EventId,
                                        "text-start",
                                        new AITextStartEventData
                                        {
                                            ProviderMetadata = new Dictionary<string, object>
                                            {
                                                ["cohere"] = new
                                                {
                                                    content_index = index
                                                }
                                            }
                                        },
                                        lastTimestamp,
                                        responseMetadata);

                                contentState.StartEmitted = true;
                            }

                            var deltaText = kind == "thinking"
                                ? GetString(contentNode, "thinking")
                                : GetString(contentNode, "text");

                            if (!string.IsNullOrEmpty(deltaText))
                            {
                                yield return kind == "thinking"
                                    ? CreateStreamEvent(
                                        providerId,
                                        contentState.EventId,
                                        "reasoning-delta",
                                        new AIReasoningDeltaEventData
                                        {
                                            Delta = deltaText,
                                            ProviderMetadata = CreateScopedProviderMetadata(providerId, new Dictionary<string, object>
                                            {
                                                ["type"] = kind,
                                                ["content_index"] = index
                                            })
                                        },
                                        lastTimestamp,
                                        responseMetadata)
                                    : CreateStreamEvent(
                                        providerId,
                                        contentState.EventId,
                                        "text-delta",
                                        new AITextDeltaEventData
                                        {
                                            Delta = deltaText,
                                            ProviderMetadata = new Dictionary<string, object>
                                            {
                                                ["cohere"] = new
                                                {
                                                    content_index = index
                                                }
                                            }
                                        },
                                        lastTimestamp,
                                        responseMetadata);
                            }

                            break;
                        }

                    case "content-end":
                        {
                            var index = GetInt32(payloadElement, "index") ?? 0;
                            if (!activeContents.TryGetValue(index, out var state))
                                break;

                            foreach (var streamEvent in FinalizeContentState(state, lastTimestamp))
                                yield return streamEvent;

                            activeContents.Remove(index);
                            break;
                        }

                    case "tool-plan-delta":
                        {
                            var toolPlanDelta = GetProperty(payloadElement, "delta") is { } planDelta
                                                && GetProperty(planDelta, "message") is { } planMessage
                                                    ? GetString(planMessage, "tool_plan")
                                                    : null;

                            if (string.IsNullOrEmpty(toolPlanDelta))
                                break;

                            if (!toolPlanState.StartEmitted)
                            {
                                yield return CreateStreamEvent(
                                    providerId,
                                    toolPlanState.EventId,
                                    "reasoning-start",
                                    new AIReasoningStartEventData
                                    {
                                        ProviderMetadata = CreateScopedProviderMetadata(providerId, new Dictionary<string, object>
                                        {
                                            ["type"] = "tool_plan"
                                        })
                                    },
                                    lastTimestamp,
                                    responseMetadata);
                                toolPlanState.StartEmitted = true;
                            }

                            yield return CreateStreamEvent(
                                providerId,
                                toolPlanState.EventId,
                                "reasoning-delta",
                                new AIReasoningDeltaEventData
                                {
                                    Delta = toolPlanDelta,
                                    ProviderMetadata = CreateScopedProviderMetadata(providerId, new Dictionary<string, object>
                                    {
                                        ["type"] = "tool_plan"
                                    })
                                },
                                lastTimestamp,
                                responseMetadata);
                            break;
                        }

                    case "tool-call-start":
                    case "tool-call-delta":
                        {
                            var index = GetInt32(payloadElement, "index") ?? 0;
                            var toolCall = GetProperty(payloadElement, "delta") is { } toolDelta
                                           && GetProperty(toolDelta, "message") is { } toolDeltaMessage
                                               ? GetProperty(toolDeltaMessage, "tool_calls") ?? payloadElement
                                               : payloadElement;

                            var toolId = GetString(toolCall, "id")
                                         ?? (activeToolsByIndex.TryGetValue(index, out var knownTool) ? knownTool.ToolCallId : null)
                                         ?? Guid.NewGuid().ToString("n");

                            var toolName = GetProperty(toolCall, "function") is { } function
                                ? GetString(function, "name")
                                : null;
                            var argumentDelta = GetProperty(toolCall, "function") is { } functionNode
                                ? GetString(functionNode, "arguments")
                                : null;

                            if (!activeToolsById.TryGetValue(toolId, out var toolState))
                            {
                                toolState = new CohereStreamingToolState
                                {
                                    ToolCallId = toolId,
                                    ToolName = toolName ?? "tool",
                                    EventId = toolId
                                };

                                activeToolsById[toolId] = toolState;
                                activeToolExecutionOrder.Add(toolId);
                            }

                            toolState.ToolName = string.IsNullOrWhiteSpace(toolName) ? toolState.ToolName : toolName!;
                            activeToolsByIndex[index] = toolState;

                            if (!toolState.StartEmitted)
                            {
                                yield return CreateStreamEvent(
                                    providerId,
                                    toolState.EventId,
                                    "tool-input-start",
                                    new AIToolInputStartEventData
                                    {
                                        ToolName = string.IsNullOrWhiteSpace(toolState.ToolName) ? "tool" : toolState.ToolName,
                                        Title = ResolveUnifiedToolTitle(request, toolState.ToolName),
                                        ProviderExecuted = false
                                    },
                                    lastTimestamp,
                                    responseMetadata);

                                toolState.StartEmitted = true;
                            }

                            if (!string.IsNullOrEmpty(argumentDelta))
                            {
                                toolState.ArgumentsBuilder.Append(argumentDelta);

                                yield return CreateStreamEvent(
                                    providerId,
                                    toolState.EventId,
                                    "tool-input-delta",
                                    new AIToolInputDeltaEventData
                                    {
                                        InputTextDelta = argumentDelta
                                    },
                                    lastTimestamp,
                                    responseMetadata);
                            }

                            break;
                        }

                    case "tool-call-end":
                        {
                            var index = GetInt32(payloadElement, "index") ?? 0;
                            if (!activeToolsByIndex.TryGetValue(index, out var toolState))
                                break;

                            foreach (var streamEvent in FinalizeToolInput(toolState, lastTimestamp))
                                yield return streamEvent;

                            activeToolsByIndex.Remove(index);
                            break;
                        }

                    case "citation-start":
                        {
                            var citation = GetProperty(payloadElement, "delta") is { } citationDelta
                                           && GetProperty(citationDelta, "message") is { } citationMessage
                                               ? GetProperty(citationMessage, "citations") ?? payloadElement
                                               : payloadElement;

                            foreach (var sourceUrlEvent in CreateCitationSourceEvents(providerId, responseId, citation, lastTimestamp, responseMetadata))
                                yield return sourceUrlEvent;

                            break;
                        }

                    case "message-end":
                        {
                            var finishReason = GetProperty(payloadElement, "delta") is { } endDelta
                                ? GetString(endDelta, "finish_reason")
                                : GetString(payloadElement, "finish_reason");

                            var usageElement = GetProperty(payloadElement, "delta") is { } deltaUsage
                                ? GetProperty(deltaUsage, "usage") ?? GetProperty(payloadElement, "usage")
                                : GetProperty(payloadElement, "usage");

                            ResolveUsage(usageElement, out var inputTokens, out var outputTokens, out var totalTokens);
                            responseMetadata = EnrichMetadataWithGatewayCost(responseMetadata, usageElement, activeModel);

                            foreach (var pending in FinalizePendingContent(lastTimestamp))
                                yield return pending;

                            foreach (var pendingTool in FinalizePendingToolCalls(lastTimestamp))
                                yield return pendingTool;

                            if (toolPlanState.StartEmitted && !toolPlanState.EndEmitted)
                            {
                                yield return CreateStreamEvent(
                                    providerId,
                                    toolPlanState.EventId,
                                    "reasoning-end",
                                    new AIReasoningEndEventData
                                    {
                                        ProviderMetadata = CreateScopedProviderMetadata(providerId, new Dictionary<string, object>
                                        {
                                            ["type"] = "tool_plan"
                                        })
                                    },
                                    lastTimestamp,
                                    responseMetadata);
                                toolPlanState.EndEmitted = true;
                            }

                            yield return CreateFinishStreamEvent(
                                providerId,
                                responseId,
                                lastTimestamp,
                                activeModel,
                                finishReason?.ToFinishReason() ?? "stop",
                                inputTokens,
                                outputTokens,
                                totalTokens,
                                responseMetadata);

                            finishEmitted = true;
                            break;
                        }
                }
            }
        }

        IEnumerable<AIStreamEvent> FinalizePendingContent(DateTimeOffset timestamp)
        {
            foreach (var state in activeContents.Values.ToList())
            {
                foreach (var streamEvent in FinalizeContentState(state, timestamp))
                    yield return streamEvent;
            }

            activeContents.Clear();
        }

        IEnumerable<AIStreamEvent> FinalizePendingToolCalls(DateTimeOffset timestamp)
        {
            foreach (var toolCallId in activeToolExecutionOrder)
            {
                if (!activeToolsById.TryGetValue(toolCallId, out var toolState))
                    continue;

                foreach (var streamEvent in FinalizeToolInput(toolState, timestamp))
                    yield return streamEvent;
            }

            activeToolsByIndex.Clear();
            activeToolsById.Clear();
            activeToolExecutionOrder.Clear();
        }

        IEnumerable<AIStreamEvent> FinalizeContentState(CohereStreamingContentState state, DateTimeOffset timestamp)
        {
            if (state.EndEmitted || !state.StartEmitted)
                yield break;

            yield return state.Kind == "thinking"
                ? CreateStreamEvent(
                    providerId,
                    state.EventId,
                    "reasoning-end",
                    new AIReasoningEndEventData
                    {
                        ProviderMetadata = CreateScopedProviderMetadata(providerId, new Dictionary<string, object>
                        {
                            ["type"] = state.Kind,
                            ["content_index"] = state.Index
                        })
                    },
                    timestamp,
                    responseMetadata)
                : CreateStreamEvent(
                    providerId,
                    state.EventId,
                    "text-end",
                    new AITextEndEventData
                    {
                        ProviderMetadata = new Dictionary<string, object>
                        {
                            ["cohere"] = new
                            {
                                content_index = state.Index
                            }
                        }
                    },
                    timestamp,
                    responseMetadata);

            state.EndEmitted = true;
        }

        IEnumerable<AIStreamEvent> FinalizeToolInput(CohereStreamingToolState toolState, DateTimeOffset timestamp)
        {
            if (toolState.InputAvailableEmitted)
                yield break;

            yield return CreateStreamEvent(
                providerId,
                toolState.EventId,
                "tool-input-available",
                new AIToolInputAvailableEventData
                {
                    ToolName = string.IsNullOrWhiteSpace(toolState.ToolName) ? "tool" : toolState.ToolName,
                    Title = ResolveUnifiedToolTitle(request, toolState.ToolName),
                    Input = ParseJsonOrText(toolState.ArgumentsBuilder.ToString())
                            ?? JsonSerializer.SerializeToElement(new { }, CohereJsonSerializerOptions),
                    ProviderExecuted = false,
                    ProviderMetadata = CreateScopedProviderMetadata(providerId, new Dictionary<string, object>
                    {
                        ["type"] = "tool_call",
                        ["tool_use_id"] = toolState.ToolCallId,
                        ["tool_name"] = toolState.ToolName,
                        ["title"] = ResolveUnifiedToolTitle(request, toolState.ToolName) ?? toolState.ToolName
                    })
                },
                timestamp,
                responseMetadata);

            toolState.InputAvailableEmitted = true;
        }
    }

    private JsonObject BuildUnifiedRequestPayload(
        AIRequest request,
        CohereProviderMetadata? providerMetadata,
        JsonObject? providerMetadataNode,
        bool stream)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new InvalidOperationException("Cohere unified requests require a model.");

        var payload = new JsonObject
        {
            ["stream"] = stream,
            ["model"] = request.Model,
            ["messages"] = BuildUnifiedMessages(request)
        };

        if (request.MaxOutputTokens is int maxTokens)
            payload["max_tokens"] = maxTokens;

        if (request.Temperature is float temperature)
            payload["temperature"] = temperature;

        if (request.TopP is double topP)
            payload["p"] = topP;

        var responseFormat = NormalizeUnifiedResponseFormat(request.ResponseFormat);
        if (responseFormat is not null)
            payload["response_format"] = responseFormat;

        if (request.Tools is { Count: > 0 })
            payload["tools"] = BuildUnifiedTools(request.Tools);

        var toolChoice = NormalizeUnifiedToolChoice(request.ToolChoice);
        if (!string.IsNullOrWhiteSpace(toolChoice))
            payload["tool_choice"] = toolChoice;

        if (providerMetadata?.Thinking is not null)
            payload["thinking"] = JsonSerializer.SerializeToNode(providerMetadata.Thinking, CohereJsonSerializerOptions);

        if (providerMetadata?.CitationOptions is not null)
            payload["citation_options"] = JsonSerializer.SerializeToNode(providerMetadata.CitationOptions, CohereJsonSerializerOptions);

        if (providerMetadata?.Priority is int priority)
            payload["priority"] = priority;

        ApplyProviderPassThrough(payload, providerMetadataNode);

        return payload;
    }

    private JsonArray BuildUnifiedMessages(AIRequest request)
    {
        var messages = new JsonArray();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.Instructions
            });
        }

        if (request.Input?.Items is { Count: > 0 })
        {
            foreach (var item in request.Input.Items)
            {
                foreach (var message in BuildUnifiedMessages(item))
                    messages.Add(message);
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Input?.Text))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = request.Input.Text
            });
        }

        if (messages.Count == 0)
            throw new InvalidOperationException("Cohere unified requests require at least one message or instructions.");

        return messages;
    }

    private IEnumerable<JsonObject> BuildUnifiedMessages(AIInputItem item)
    {
        var role = NormalizeInputRole(item.Role);
        var toolParts = item.Content?.OfType<AIToolCallContentPart>().ToList() ?? [];

        switch (role)
        {
            case "system":
                {
                    var text = ExtractTextContent(item.Content);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        yield return new JsonObject
                        {
                            ["role"] = "system",
                            ["content"] = text
                        };
                    }

                    yield break;
                }

            case "user":
                {
                    var content = BuildUserContent(item.Content);
                    if (content is not null)
                    {
                        yield return new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = content
                        };
                    }

                    yield break;
                }

            case "assistant":
                {
                    foreach (var assistantMessage in BuildAssistantReplayMessages(item.Content))
                        yield return assistantMessage;

                    yield break;
                }

            case "tool":
                {
                    foreach (var toolMessage in BuildToolMessages(toolParts))
                        yield return toolMessage;

                    yield break;
                }

            default:
                {
                    var fallbackContent = BuildUserContent(item.Content);
                    if (fallbackContent is not null)
                    {
                        yield return new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = fallbackContent
                        };
                    }

                    yield break;
                }
        }
    }

    private JsonNode? BuildUserContent(List<AIContentPart>? parts)
    {
        if (parts is null || parts.Count == 0)
            return null;

        var blocks = new JsonArray();

        foreach (var part in parts)
        {
            switch (part)
            {
                case AITextContentPart textPart when !string.IsNullOrWhiteSpace(textPart.Text):
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = textPart.Text
                    });
                    break;

                case AIReasoningContentPart reasoningPart when !string.IsNullOrWhiteSpace(reasoningPart.Text):
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = reasoningPart.Text
                    });
                    break;

                case AIFileContentPart filePart when TryBuildImageContent(filePart) is { } imageContent:
                    blocks.Add(imageContent);
                    break;

                case AIFileContentPart:
                    throw new NotSupportedException("Cohere v2 chat only supports image files inside user content blocks.");
            }
        }

        return blocks.Count switch
        {
            0 => null,
            1 when blocks[0] is JsonObject singleBlock
                 && string.Equals(singleBlock["type"]?.GetValue<string>(), "text", StringComparison.OrdinalIgnoreCase)
                => singleBlock["text"]?.DeepClone(),
            _ => blocks
        };
    }

    private JsonNode? BuildAssistantContent(List<AIContentPart>? parts)
    {
        if (parts is null || parts.Count == 0)
            return null;

        var thinkingBlocks = new List<JsonObject>();
        var textBlocks = new List<JsonObject>();

        foreach (var part in parts)
        {
            switch (part)
            {
                case AITextContentPart textPart when !string.IsNullOrWhiteSpace(textPart.Text):
                    textBlocks.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = textPart.Text
                    });
                    break;

                case AIReasoningContentPart reasoningPart when !string.IsNullOrWhiteSpace(reasoningPart.Text):
                    thinkingBlocks.Add(new JsonObject
                    {
                        ["type"] = "thinking",
                        ["thinking"] = reasoningPart.Text
                    });
                    break;
            }
        }

        var blocks = new JsonArray();
        foreach (var block in thinkingBlocks)
            blocks.Add(block);
        foreach (var block in textBlocks)
            blocks.Add(block);

        return blocks.Count switch
        {
            0 => null,
            1 when blocks[0] is JsonObject singleBlock
                 && string.Equals(singleBlock["type"]?.GetValue<string>(), "text", StringComparison.OrdinalIgnoreCase)
                => singleBlock["text"]?.DeepClone(),
            _ => blocks
        };
    }

    private IEnumerable<JsonObject> BuildAssistantReplayMessages(List<AIContentPart>? parts)
    {
        if (parts is null || parts.Count == 0)
            yield break;

        var pendingContent = new List<AIContentPart>();
        var pendingToolParts = new List<AIToolCallContentPart>();

        foreach (var part in parts)
        {
            if (part is AIToolCallContentPart toolPart)
            {
                pendingToolParts.Add(toolPart);
                continue;
            }

            if (pendingToolParts.Count > 0)
            {
                foreach (var message in FlushAssistantReplaySegment(pendingContent, pendingToolParts))
                    yield return message;

                pendingContent = new List<AIContentPart>();
                pendingToolParts = new List<AIToolCallContentPart>();
            }

            pendingContent.Add(part);
        }

        foreach (var message in FlushAssistantReplaySegment(pendingContent, pendingToolParts))
            yield return message;
    }

    private IEnumerable<JsonObject> FlushAssistantReplaySegment(
        List<AIContentPart> contentParts,
        List<AIToolCallContentPart> toolParts)
    {
        var assistantContent = BuildAssistantContent(contentParts);
        var toolCalls = BuildAssistantToolCalls(toolParts);

        if (assistantContent is not null || toolCalls is not null)
        {
            var assistantMessage = new JsonObject
            {
                ["role"] = "assistant"
            };

            if (assistantContent is not null)
                assistantMessage["content"] = assistantContent;

            if (toolCalls is not null)
                assistantMessage["tool_calls"] = toolCalls;

            yield return assistantMessage;
        }

        foreach (var toolMessage in BuildToolMessages(toolParts))
            yield return toolMessage;
    }

    private JsonArray? BuildAssistantToolCalls(IEnumerable<AIToolCallContentPart>? toolParts)
    {
        var toolCalls = new JsonArray();

        foreach (var toolPart in toolParts ?? Enumerable.Empty<AIToolCallContentPart>())
        {
            if (!ShouldEmitAssistantToolCall(toolPart))
                continue;

            toolCalls.Add(new JsonObject
            {
                ["id"] = string.IsNullOrWhiteSpace(toolPart.ToolCallId) ? Guid.NewGuid().ToString("n") : toolPart.ToolCallId,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = toolPart.ToolName ?? toolPart.Title ?? "tool",
                    ["arguments"] = SerializeToolPayload(toolPart.Input, "{}")
                }
            });
        }

        return toolCalls.Count == 0 ? null : toolCalls;
    }

    private IEnumerable<JsonObject> BuildToolMessages(IEnumerable<AIToolCallContentPart>? toolParts)
    {
        foreach (var toolPart in toolParts ?? Enumerable.Empty<AIToolCallContentPart>())
        {
            if (!ShouldEmitToolResultMessage(toolPart))
                continue;

            yield return new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = toolPart.ToolCallId,
                ["content"] = SerializeToolPayload(toolPart.Output, "{}")
            };
        }
    }

    private static bool ShouldEmitAssistantToolCall(AIToolCallContentPart toolPart)
    {
        if (toolPart.ProviderExecuted == true)
            return false;

        if (string.IsNullOrWhiteSpace(toolPart.ToolCallId))
            return false;

        return NormalizeToolReplayState(toolPart.State) switch
        {
            "input-available" => true,
            "approval-requested" => true,
            "approval-responded" => true,
            "output-available" => true,
            "output-error" => true,
            _ => true
        };
    }

    private static bool ShouldEmitToolResultMessage(AIToolCallContentPart toolPart)
    {
        if (toolPart.ProviderExecuted == true)
            return false;

        if (string.IsNullOrWhiteSpace(toolPart.ToolCallId))
            return false;

        if (toolPart.Output is null)
            return false;

        return NormalizeToolReplayState(toolPart.State) switch
        {
            "input-available" => false,
            "approval-requested" => false,
            "approval-responded" => true,
            "output-available" => true,
            "output-error" => true,
            _ => true
        };
    }

    private static string NormalizeToolReplayState(string? state)
        => string.IsNullOrWhiteSpace(state)
            ? string.Empty
            : state.Trim().ToLowerInvariant();

    private JsonArray BuildUnifiedTools(IEnumerable<AIToolDefinition> tools)
    {
        var array = new JsonArray();

        foreach (var tool in tools)
        {
            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = TryConvertToJsonNode(tool.InputSchema)
                                    ?? JsonSerializer.SerializeToNode(new { type = "object", properties = new { } }, CohereJsonSerializerOptions)
                }
            });
        }

        return array;
    }

    private AIResponse CreateUnifiedResponse(AIRequest request, JsonObject payload, JsonElement response)
    {
        var outputItems = new List<AIOutputItem>();
        var sources = new List<Dictionary<string, object?>>();
        var responseModel = GetString(response, "model") ?? request.Model;
        JsonElement? responseUsage = response.TryGetProperty("usage", out var usageElement)
            ? usageElement.Clone()
            : null;

        if (response.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object)
        {
            var contentParts = new List<AIContentPart>();

            if (message.TryGetProperty("content", out var contentElement))
                AddAssistantContentParts(contentElement, contentParts);

            if (message.TryGetProperty("tool_plan", out var toolPlanElement)
                && toolPlanElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(toolPlanElement.GetString()))
            {
                contentParts.Add(new AIReasoningContentPart
                {
                    Type = "reasoning",
                    Text = toolPlanElement.GetString(),
                    Metadata = new Dictionary<string, object?>
                    {
                        ["cohere.reasoning.type"] = "tool_plan"
                    }
                });
            }

            if (message.TryGetProperty("tool_calls", out var toolCallsElement)
                && toolCallsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolCallPart in ParseAssistantToolCalls(toolCallsElement))
                    contentParts.Add(toolCallPart);
            }

            if (message.TryGetProperty("citations", out var citationsElement)
                && citationsElement.ValueKind == JsonValueKind.Array)
            {
                sources.AddRange(FlattenCitationSources(citationsElement));
            }

            outputItems.Add(new AIOutputItem
            {
                Type = "message",
                Role = GetString(message, "role") ?? "assistant",
                Content = contentParts,
                Metadata = new Dictionary<string, object?>
                {
                    ["cohere.message.raw"] = message.Clone(),
                    ["cohere.citations"] = message.TryGetProperty("citations", out var rawCitations) ? rawCitations.Clone() : null,
                    ["cohere.sources"] = sources.Count == 0 ? null : JsonSerializer.SerializeToElement(sources, CohereJsonSerializerOptions)
                }
            });
        }

        var finishReason = response.TryGetProperty("finish_reason", out var finishReasonElement)
            ? finishReasonElement.GetString()
            : null;

        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["cohere.id"] = response.TryGetProperty("id", out var idElement) ? idElement.GetString() : null,
            ["cohere.requested_model"] = request.Model,
            ["cohere.finish_reason"] = finishReason,
            ["cohere.request"] = JsonSerializer.SerializeToElement(payload, CohereJsonSerializerOptions),
            ["cohere.replay.input_items"] = request.Input?.Items is { Count: > 0 } inputItems
                ? JsonSerializer.SerializeToElement(inputItems, CohereJsonSerializerOptions)
                : null,
            ["cohere.replay.messages"] = payload["messages"]?.DeepClone(),
            ["cohere.response"] = response.Clone(),
            ["cohere.usage"] = responseUsage,
            ["cohere.sources"] = sources.Count == 0 ? null : JsonSerializer.SerializeToElement(sources, CohereJsonSerializerOptions),
            ["unified.request.headers"] = request.Headers?.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)
        };

        metadata = EnrichMetadataWithGatewayCost(metadata, responseUsage, responseModel);

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model,
            Status = InferUnifiedStatus(finishReason),
            Output = new AIOutput
            {
                Items = outputItems,
                Metadata = new Dictionary<string, object?>
                {
                    ["cohere.sources"] = sources.Count == 0 ? null : JsonSerializer.SerializeToElement(sources, CohereJsonSerializerOptions)
                }
            },
            Usage = responseUsage,
            Metadata = metadata
        };
    }

    private static void AddAssistantContentParts(JsonElement contentElement, List<AIContentPart> contentParts)
    {
        if (contentElement.ValueKind == JsonValueKind.String)
        {
            var text = contentElement.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                contentParts.Add(new AITextContentPart
                {
                    Text = text,
                    Type = "text",
                    Metadata = null
                });
            }

            return;
        }

        if (contentElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in contentElement.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;

            switch (GetString(block, "type"))
            {
                case "text":
                    {
                        var text = GetString(block, "text");
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            contentParts.Add(new AITextContentPart
                            {
                                Text = text,
                                Type = "text",
                                Metadata = new Dictionary<string, object?>
                                {
                                    ["cohere.content.raw"] = block.Clone()
                                }
                            });
                        }

                        break;
                    }

                case "thinking":
                    {
                        var thinking = GetString(block, "thinking");
                        if (!string.IsNullOrWhiteSpace(thinking))
                        {
                            contentParts.Add(new AIReasoningContentPart
                            {
                                Text = thinking,
                                Type = "reasoning",
                                Metadata = new Dictionary<string, object?>
                                {
                                    ["cohere.content.raw"] = block.Clone(),
                                    ["cohere.reasoning.type"] = "thinking"
                                }
                            });
                        }

                        break;
                    }
            }
        }
    }

    private static IEnumerable<AIToolCallContentPart> ParseAssistantToolCalls(JsonElement toolCallsElement)
    {
        foreach (var toolCall in toolCallsElement.EnumerateArray())
        {
            if (toolCall.ValueKind != JsonValueKind.Object)
                continue;

            var functionElement = GetProperty(toolCall, "function");
            var toolCallId = GetString(toolCall, "id") ?? Guid.NewGuid().ToString("n");
            var toolName = functionElement is { ValueKind: JsonValueKind.Object }
                ? GetString(functionElement.Value, "name")
                : null;

            yield return new AIToolCallContentPart
            {
                Type = "tool-input-available",
                ToolCallId = toolCallId,
                ToolName = toolName,
                Title = toolName,
                Input = functionElement is { ValueKind: JsonValueKind.Object }
                    ? ParseJsonOrText(GetString(functionElement.Value, "arguments"))
                      ?? JsonSerializer.SerializeToElement(new { }, CohereJsonSerializerOptions)
                    : JsonSerializer.SerializeToElement(new { }, CohereJsonSerializerOptions),
                State = "input-available",
                ProviderExecuted = false,
                Metadata = new Dictionary<string, object?>
                {
                    ["cohere.tool_call.raw"] = toolCall.Clone()
                }
            };
        }
    }

    private static List<Dictionary<string, object?>> FlattenCitationSources(JsonElement citationsElement)
    {
        var sources = new List<Dictionary<string, object?>>();

        foreach (var citation in citationsElement.EnumerateArray())
        {
            if (citation.ValueKind != JsonValueKind.Object)
                continue;

            var citationText = GetString(citation, "text");
            var citationType = GetString(citation, "type");
            var contentIndex = GetInt32(citation, "content_index");

            if (!citation.TryGetProperty("sources", out var sourceArray)
                || sourceArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var source in sourceArray.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object)
                    continue;

                sources.Add(new Dictionary<string, object?>
                {
                    ["source_id"] = GetString(source, "id"),
                    ["source_type"] = GetString(source, "type"),
                    ["url"] = TryResolveCitationUrl(source),
                    ["title"] = TryResolveCitationTitle(source),
                    ["citation_text"] = citationText,
                    ["citation_type"] = citationType,
                    ["content_index"] = contentIndex,
                    ["raw"] = source.Clone()
                });
            }
        }

        return sources;
    }

    private static IEnumerable<AIStreamEvent> CreateCitationSourceEvents(
        string providerId,
        string responseId,
        JsonElement citation,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata)
    {
        if (!citation.TryGetProperty("sources", out var sourcesElement)
            || sourcesElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var citationText = GetString(citation, "text");
        var citationType = GetString(citation, "type");
        var contentIndex = GetInt32(citation, "content_index");

        foreach (var source in sourcesElement.EnumerateArray())
        {
            var url = TryResolveCitationUrl(source);
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var sourceId = GetString(source, "id") ?? url;
            var sourceType = GetString(source, "type");
            var title = TryResolveCitationTitle(source);

            yield return CreateStreamEvent(
                providerId,
                $"{responseId}:source:{sourceId}",
                "source-url",
                new AISourceUrlEventData
                {
                    SourceId = sourceId,
                    Url = url,
                    Title = title,
                    Type = sourceType,
                    ProviderMetadata = CreateScopedProviderMetadata(providerId, new Dictionary<string, object>
                    {
                        ["type"] = sourceType ?? "citation",
                        ["citation_text"] = citationText ?? string.Empty,
                        ["citation_type"] = citationType ?? string.Empty,
                        ["content_index"] = contentIndex ?? 0,
                        ["source_id"] = sourceId
                    })
                },
                timestamp,
                metadata);
        }
    }

    private HttpRequestMessage CreateUnifiedHttpRequest(JsonObject payload, bool stream, Dictionary<string, string>? headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "v2/chat")
        {
            Content = new StringContent(payload.ToJsonString(CohereJsonSerializerOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? MediaTypeNames.Text.EventStream : MediaTypeNames.Application.Json));

        ApplyRequestHeaders(request, headers);
        return request;
    }

    private static void ApplyRequestHeaders(HttpRequestMessage request, Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return;

        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private CohereProviderMetadata? GetUnifiedProviderMetadata(AIRequest request)
        => request.Metadata.GetProviderMetadata<CohereProviderMetadata>(GetIdentifier());

    private JsonObject? GetUnifiedProviderMetadataNode(AIRequest request)
    {
        if (request.Metadata is null
            || !request.Metadata.TryGetValue(GetIdentifier(), out var providerValue)
            || providerValue is null)
        {
            return null;
        }

        return TryConvertToJsonObject(providerValue);
    }

    private static void ApplyProviderPassThrough(JsonObject payload, JsonObject? providerMetadata)
    {
        if (providerMetadata is null)
            return;

        foreach (var propertyName in new[]
                 {
                     "documents",
                     "strict_tools",
                     "safety_mode",
                     "stop_sequences",
                     "seed",
                     "frequency_penalty",
                     "presence_penalty",
                     "k",
                     "logprobs",
                     "citation_options",
                     "thinking",
                     "priority"
                 })
        {
            if (payload[propertyName] is not null)
                continue;

            if (providerMetadata[propertyName] is { } propertyValue)
                payload[propertyName] = propertyValue.DeepClone();
        }
    }

    private static string NormalizeInputRole(string? role)
        => string.IsNullOrWhiteSpace(role)
            ? "user"
            : role.Trim().ToLowerInvariant();

    private static string? ExtractTextContent(List<AIContentPart>? parts)
    {
        if (parts is null || parts.Count == 0)
            return null;

        var textParts = new List<string>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case AITextContentPart textPart when !string.IsNullOrWhiteSpace(textPart.Text):
                    textParts.Add(textPart.Text);
                    break;
                case AIReasoningContentPart reasoningPart when !string.IsNullOrWhiteSpace(reasoningPart.Text):
                    textParts.Add(reasoningPart.Text!);
                    break;
            }
        }

        return textParts.Count == 0 ? null : string.Join("\n\n", textParts);
    }

    private static JsonObject? TryBuildImageContent(AIFileContentPart filePart)
    {
        var url = filePart.Data?.ToString();
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var mediaType = filePart.MediaType;
        var isImage = !string.IsNullOrWhiteSpace(mediaType)
                      ? mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                      : url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);

        if (!isImage)
            return null;

        return new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject
            {
                ["url"] = url,
                ["detail"] = "high"
            }
        };
    }

    private static JsonNode? NormalizeUnifiedResponseFormat(object? responseFormat)
    {
        if (responseFormat is null)
            return null;

        if (TryConvertToJsonObject(responseFormat) is not { } formatObject)
            return null;

        var type = formatObject["type"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(type))
            return formatObject;

        if (string.Equals(type, "json_schema", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = new JsonObject
            {
                ["type"] = "json_object"
            };

            if (formatObject["schema"] is { } schemaNode)
                normalized["json_schema"] = schemaNode.DeepClone();
            else if (formatObject["json_schema"] is { } existingSchema)
                normalized["json_schema"] = existingSchema.DeepClone();

            return normalized;
        }

        return formatObject;
    }

    private static string? NormalizeUnifiedToolChoice(object? toolChoice)
    {
        if (toolChoice is null)
            return null;

        string? raw = toolChoice switch
        {
            string text => text,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            JsonElement json when json.ValueKind == JsonValueKind.Object && json.TryGetProperty("type", out var typeElement) => typeElement.GetString(),
            JsonNode node => node["type"]?.GetValue<string>(),
            _ => TryConvertToJsonObject(toolChoice)?["type"]?.GetValue<string>()
        };

        return raw?.Trim().ToLowerInvariant() switch
        {
            "required" => "REQUIRED",
            "none" => "NONE",
            _ => null
        };
    }

    private static string InferUnifiedStatus(string? finishReason)
        => finishReason?.Trim().ToUpperInvariant() switch
        {
            "ERROR" or "TIMEOUT" => "failed",
            _ => "completed"
        };

    private static void EnsureUnifiedSuccess(HttpResponseMessage response, string responseBody)
    {
        if (response.IsSuccessStatusCode)
            return;

        throw new HttpRequestException(BuildUnifiedErrorMessage(response, responseBody));
    }

    private static string BuildUnifiedErrorMessage(HttpResponseMessage response, string responseBody)
        => $"Cohere unified request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
           + (string.IsNullOrWhiteSpace(responseBody) ? string.Empty : $" Body: {responseBody}");

    private static JsonElement UnwrapJsonEnvelope(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Object
            && GetString(raw, "type") == "json"
            && raw.TryGetProperty("value", out var valueElement))
        {
            return valueElement.Clone();
        }

        return raw;
    }

    private static string? ResolveStreamEventName(JsonElement raw, string? sseEventName)
        => !string.IsNullOrWhiteSpace(sseEventName)
            ? sseEventName
            : GetString(raw, "type") ?? GetString(raw, "event");

    private static JsonElement ResolveStreamPayload(JsonElement raw)
        => raw.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : raw;

    private static string? DetermineContentKind(JsonElement contentNode, string? existingKind)
    {
        var explicitType = GetString(contentNode, "type");
        if (string.Equals(explicitType, "thinking", StringComparison.OrdinalIgnoreCase))
            return "thinking";

        if (string.Equals(explicitType, "text", StringComparison.OrdinalIgnoreCase))
            return "text";

        if (GetString(contentNode, "thinking") is not null)
            return "thinking";

        if (GetString(contentNode, "text") is not null)
            return "text";

        return existingKind;
    }

    private static void ResolveUsage(JsonElement? usageElement, out int? inputTokens, out int? outputTokens, out int? totalTokens)
    {
        inputTokens = null;
        outputTokens = null;
        totalTokens = null;

        if (usageElement is not { ValueKind: JsonValueKind.Object } usage)
            return;

        if (usage.TryGetProperty("tokens", out var tokensElement)
            && tokensElement.ValueKind == JsonValueKind.Object)
        {
            inputTokens = GetInt32(tokensElement, "input_tokens");
            outputTokens = GetInt32(tokensElement, "output_tokens");
            totalTokens = GetInt32(tokensElement, "total_tokens") ?? ((inputTokens ?? 0) + (outputTokens ?? 0));
            return;
        }

        inputTokens = GetInt32(usage, "input_tokens");
        outputTokens = GetInt32(usage, "output_tokens");
        totalTokens = GetInt32(usage, "total_tokens") ?? ((inputTokens ?? 0) + (outputTokens ?? 0));
    }

    private ModelPricing? ResolveCatalogPricing(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var pricing = GetIdentifier().GetPricing();
        if (pricing is null || pricing.Count == 0)
            return null;

        var candidates = new[]
        {
            modelId,
            modelId.ToModelId(GetIdentifier()),
            modelId.StartsWith($"{GetIdentifier()}/", StringComparison.OrdinalIgnoreCase)
                ? modelId[(GetIdentifier().Length + 1)..]
                : null
        }
        .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (candidate is not null && pricing.TryGetValue(candidate, out var modelPricing))
                return modelPricing;
        }

        return null;
    }

    private Dictionary<string, object?> EnrichMetadataWithGatewayCost(
        Dictionary<string, object?> metadata,
        JsonElement? usageElement,
        string? modelId)
    {
        if (usageElement is not { ValueKind: JsonValueKind.Object } usage)
            return metadata;

        var pricing = ResolveCatalogPricing(modelId);
        if (pricing is null)
            return metadata;

        var normalizedUsage = CreateCostUsageObject(usage);
        if (normalizedUsage is null)
            return metadata;

        return ModelCostMetadataEnricher.AddCostFromUsage(normalizedUsage, metadata, pricing);
    }

    private static Dictionary<string, object?>? CreateCostUsageObject(JsonElement usage)
    {
        int? inputTokens = null;
        int? outputTokens = null;
        int? totalTokens = null;
        int? cachedInputTokens = null;

        var useBilledUnits = usage.TryGetProperty("billed_units", out var billedUnits)
            && billedUnits.ValueKind == JsonValueKind.Object;

        if (useBilledUnits)
        {
            inputTokens = GetInt32(billedUnits, "input_tokens");
            outputTokens = GetInt32(billedUnits, "output_tokens");
            totalTokens = (inputTokens ?? 0) + (outputTokens ?? 0);
        }

        if (!useBilledUnits || inputTokens is null)
        {
            ResolveUsage(usage, out var fallbackInputTokens, out var fallbackOutputTokens, out var fallbackTotalTokens);
            inputTokens = fallbackInputTokens;
            outputTokens = fallbackOutputTokens;
            totalTokens = fallbackTotalTokens;

            if (usage.TryGetProperty("tokens", out var tokensElement)
                && tokensElement.ValueKind == JsonValueKind.Object)
            {
                cachedInputTokens = GetInt32(tokensElement, "cached_tokens");
            }
        }

        if (inputTokens is null)
            return null;

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["input_tokens"] = inputTokens.Value,
            ["output_tokens"] = outputTokens ?? 0,
            ["total_tokens"] = totalTokens ?? (inputTokens.Value + (outputTokens ?? 0)),
            ["cached_input_tokens"] = cachedInputTokens
        };
    }

    private static AIStreamEvent CreateDataStreamEvent(
        string providerId,
        string type,
        object payload,
        Dictionary<string, string>? headers = null,
        Dictionary<string, object?>? metadata = null)
    {
        var finalMetadata = metadata is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase);

        if (headers is { Count: > 0 })
            finalMetadata["unified.request.headers"] = headers.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);

        return new AIStreamEvent
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Timestamp = DateTimeOffset.UtcNow,
                Data = new AIDataEventData
                {
                    Data = payload,
                    Transient = type.Contains("chunk"),
                }
            },
            Metadata = finalMetadata.Count == 0 ? null : finalMetadata
        };
    }

    private static AIStreamEvent CreateStreamEvent(
        string providerId,
        string? eventId,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = eventId,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static AIStreamEvent CreateFinishStreamEvent(
        string providerId,
        string? eventId,
        DateTimeOffset timestamp,
        string? model,
        string? finishReason,
        int? inputTokens,
        int? outputTokens,
        int? totalTokens,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = eventId,
                Timestamp = timestamp,
                Metadata = metadata,
                Data = new AIFinishEventData
                {
                    FinishReason = finishReason,
                    Model = model,
                    CompletedAt = timestamp.ToUnixTimeSeconds(),
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = totalTokens,
                    MessageMetadata = ToMessageMetadata(metadata)
                }
            },
            Metadata = metadata
        };

    private static Dictionary<string, object>? ToMessageMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in metadata)
        {
            if (item.Value is not null)
                result[item.Key] = item.Value;
        }

        return result.Count == 0 ? null : result;
    }

    private static Dictionary<string, Dictionary<string, object>> CreateScopedProviderMetadata(string providerId, Dictionary<string, object> metadata)
        => new()
        {
            [providerId] = metadata
        };

    private static string? ResolveUnifiedToolTitle(AIRequest request, string? toolName)
        => string.IsNullOrWhiteSpace(toolName)
            ? null
            : request.Tools?.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.OrdinalIgnoreCase))?.Title
              ?? toolName;

    private static string SerializeToolPayload(object? payload, string fallback)
    {
        if (payload is null)
            return fallback;

        if (payload is string text)
            return string.IsNullOrWhiteSpace(text) ? fallback : text;

        try
        {
            return payload switch
            {
                JsonElement jsonElement => jsonElement.GetRawText(),
                JsonNode jsonNode => jsonNode.ToJsonString(CohereJsonSerializerOptions),
                _ => JsonSerializer.Serialize(payload, CohereJsonSerializerOptions)
            };
        }
        catch
        {
            return fallback;
        }
    }

    private static object? ParseJsonOrText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return JsonDocument.Parse(value).RootElement.Clone();
        }
        catch
        {
            return value;
        }
    }

    private static JsonObject? TryConvertToJsonObject(object? value)
    {
        var node = TryConvertToJsonNode(value);
        return node as JsonObject;
    }

    private static JsonNode? TryConvertToJsonNode(object? value)
    {
        try
        {
            return value switch
            {
                null => null,
                JsonNode node => node.DeepClone(),
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                string text when !string.IsNullOrWhiteSpace(text)
                    && (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')) => JsonNode.Parse(text),
                _ => JsonSerializer.SerializeToNode(value, CohereJsonSerializerOptions)
            };
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? GetProperty(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var propertyValue)
            ? propertyValue.Clone()
            : null;

    private static string? GetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var propertyValue)
           && propertyValue.ValueKind == JsonValueKind.String
            ? propertyValue.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var propertyValue))
        {
            return null;
        }

        return propertyValue.ValueKind switch
        {
            JsonValueKind.Number when propertyValue.TryGetInt32(out var value) => value,
            JsonValueKind.Number => (int?)propertyValue.GetDouble(),
            _ => null
        };
    }

    private static string? TryResolveCitationUrl(JsonElement source)
    {
        if (GetProperty(source, "document") is { } documentElement)
        {
            var documentUrl = GetString(documentElement, "url")
                              ?? GetString(documentElement, "source_url")
                              ?? GetString(documentElement, "href");
            if (!string.IsNullOrWhiteSpace(documentUrl))
                return documentUrl;
        }

        if (GetProperty(source, "tool_output") is { } toolOutputElement)
        {
            var toolUrl = GetString(toolOutputElement, "url")
                          ?? GetString(toolOutputElement, "source_url")
                          ?? GetString(toolOutputElement, "href");
            if (!string.IsNullOrWhiteSpace(toolUrl))
                return toolUrl;
        }

        return GetString(source, "url")
               ?? GetString(source, "source_url")
               ?? GetString(source, "href");
    }

    private static string? TryResolveCitationTitle(JsonElement source)
    {
        if (GetProperty(source, "document") is { } documentElement)
        {
            var documentTitle = GetString(documentElement, "title")
                                ?? GetString(documentElement, "name")
                                ?? GetString(documentElement, "content");
            if (!string.IsNullOrWhiteSpace(documentTitle))
                return documentTitle;
        }

        if (GetProperty(source, "tool_output") is { } toolOutputElement)
        {
            var toolTitle = GetString(toolOutputElement, "title")
                            ?? GetString(toolOutputElement, "name");
            if (!string.IsNullOrWhiteSpace(toolTitle))
                return toolTitle;
        }

        return GetString(source, "title")
               ?? GetString(source, "name")
               ?? GetString(source, "id");
    }

    private sealed class CohereStreamingContentState
    {
        public int Index { get; init; }

        public string Kind { get; set; } = "text";

        public string EventId { get; init; } = Guid.NewGuid().ToString("n");

        public bool StartEmitted { get; set; }

        public bool EndEmitted { get; set; }
    }

    private sealed class CohereStreamingToolState
    {
        public string ToolCallId { get; init; } = Guid.NewGuid().ToString("n");

        public string ToolName { get; set; } = "tool";

        public string EventId { get; init; } = Guid.NewGuid().ToString("n");

        public StringBuilder ArgumentsBuilder { get; } = new();

        public bool StartEmitted { get; set; }

        public bool InputAvailableEmitted { get; set; }
    }
}
