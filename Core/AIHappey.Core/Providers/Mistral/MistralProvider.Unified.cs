using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model.Providers.Mistral;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider : IModelProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = ResolveConversationTarget(request.Model);
        var providerMetadata = GetUnifiedProviderMetadata(request);
        var capture = request.GetMistralBackendCapture(GetIdentifier());
        var conversationRequest = BuildUnifiedConversationRequest(request, target, providerMetadata, stream: false);
        var response = await StartConversationAsync(conversationRequest, cancellationToken, capture);

        return await CreateUnifiedResponseAsync(request, response, target, cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var providerId = GetIdentifier();
        var target = ResolveConversationTarget(request.Model);
        var providerMetadata = GetUnifiedProviderMetadata(request);
        var capture = request.GetMistralBackendCapture(providerId);
        var conversationRequest = BuildUnifiedConversationRequest(request, target, providerMetadata, stream: true);

        var responseEventId = request.Id ?? Guid.NewGuid().ToString("n");
        var textStarted = false;
        var finishEmitted = false;
        var activeModel = target.ExposedModelId;
        var lastTimestamp = DateTimeOffset.UtcNow;
        var responseMetadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["mistral.requested_model"] = request.Model,
            ["mistral.target_model"] = target.Model,
            ["mistral.target_agent_id"] = target.AgentId
        };

        var activeToolExecutions = new Dictionary<string, UnifiedStreamingToolState>(StringComparer.Ordinal);
        var activeToolExecutionOrder = new List<string>();
        MistralConversationUsage? usage = null;

        await foreach (var evt in StartConversationStreamAsync(conversationRequest, cancellationToken, capture))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastTimestamp = DateTimeOffset.UtcNow;

            switch (evt.Type)
            {
                case "conversation.response.started":
                    responseMetadata["mistral.response.started"] = true;
                    if (!string.IsNullOrWhiteSpace(evt.GetString("conversation_id")))
                        responseMetadata["mistral.conversation_id"] = evt.GetString("conversation_id");
                    break;

                case "message.output.delta":
                    {
                        activeModel = NormalizeReportedModel(evt.GetString("model"), target);

                        foreach (var part in EnumerateContentParts(evt.GetNode("content")))
                        {
                            if (part.Type is "output_text" or "text")
                            {
                                if (!string.IsNullOrEmpty(part.Text))
                                {
                                    if (!textStarted)
                                    {
                                        yield return CreateStreamEvent(
                                            providerId,
                                            responseEventId,
                                            "text-start",
                                            new AITextStartEventData
                                            {
                                                //      ProviderMetadata = ToProviderMetadata(responseMetadata)
                                            },
                                            lastTimestamp,
                                            responseMetadata);
                                        textStarted = true;
                                    }

                                    yield return CreateStreamEvent(
                                        providerId,
                                        responseEventId,
                                        "text-delta",
                                        new AITextDeltaEventData
                                        {
                                            Delta = part.Text,
                                            //                                        ProviderMetadata = ToProviderMetadata(responseMetadata)
                                        },
                                        lastTimestamp,
                                        responseMetadata);
                                }

                                continue;
                            }

                            if ((part.Type == "tool_reference" || part.Type == "document_url")
                                && TryGetValidSourceUrl(part.Url, out var sourceUrl) && !string.IsNullOrEmpty(sourceUrl))
                            {
                                yield return CreateSourceUrlEvent(providerId, responseEventId, sourceUrl, part, lastTimestamp, responseMetadata);
                                continue;
                            }

                            if (part.Type != "tool_file")
                                continue;

                            var download = await TryDownloadConversationFileAsync(part.FileId, part.FileType, cancellationToken);
                            if (download.File is not null)
                            {
                                yield return CreateFileStreamEvent(providerId, responseEventId, download.File, lastTimestamp, responseMetadata);
                            }
                            else if (!string.IsNullOrWhiteSpace(download.Error))
                            {
                                yield return CreateToolOutputErrorStreamEvent(
                                    providerId,
                                    part.FileId,
                                    download.Error,
                                    providerExecuted: true,
                                    timestamp: lastTimestamp,
                                    metadata: responseMetadata);
                            }
                        }

                        break;
                    }

                case "tool.execution.delta":
                case "function.call.delta":
                    {
                        var toolCallId = evt.GetString("tool_call_id")
                                         ?? evt.GetString("id")
                                         ?? Guid.NewGuid().ToString("n");
                        var toolName = evt.GetString("name") ?? string.Empty;
                        var inputDelta = ReadNodeAsString(evt.GetNode("arguments"));
                        var toolState = GetOrCreateStreamingToolState(activeToolExecutions, activeToolExecutionOrder, toolCallId, toolName);

                        if (!string.IsNullOrWhiteSpace(toolName))
                        {
                            toolState.ToolName = toolName;
                            toolState.ProviderExecuted = IsProviderExecutedTool(toolName);
                        }

                        if (!toolState.StartEmitted)
                        {
                            yield return CreateStreamEvent(
                                providerId,
                                toolCallId,
                                "tool-input-start",
                                new AIToolInputStartEventData
                                {
                                    ToolName = string.IsNullOrWhiteSpace(toolState.ToolName) ? "tool" : toolState.ToolName,
                                    Title = ResolveUnifiedToolTitle(request, toolState.ToolName),
                                    ProviderExecuted = toolState.ProviderExecuted
                                },
                                lastTimestamp,
                                responseMetadata);
                            toolState.StartEmitted = true;
                        }

                        if (!string.IsNullOrEmpty(inputDelta))
                        {
                            toolState.InputBuilder.Append(inputDelta);
                            yield return CreateStreamEvent(
                                providerId,
                                toolCallId,
                                "tool-input-delta",
                                new AIToolInputDeltaEventData
                                {
                                    InputTextDelta = inputDelta
                                },
                                lastTimestamp,
                                responseMetadata);
                        }

                        break;
                    }

                case "tool.execution.started":
                case "agent.handoff.started":
                    {
                        var toolCallId = evt.GetString("tool_call_id")
                                         ?? evt.GetString("id")
                                         ?? Guid.NewGuid().ToString("n");
                        var toolName = evt.GetString("name") ?? string.Empty;
                        var inputText = ReadNodeAsString(evt.GetNode("arguments"));
                        var toolState = GetOrCreateStreamingToolState(activeToolExecutions, activeToolExecutionOrder, toolCallId, toolName);

                        toolState.ToolName = string.IsNullOrWhiteSpace(toolName) ? toolState.ToolName : toolName;
                        toolState.ProviderExecuted = IsProviderExecutedTool(toolState.ToolName);

                        if (!toolState.StartEmitted)
                        {
                            yield return CreateStreamEvent(
                                providerId,
                                toolCallId,
                                "tool-input-start",
                                new AIToolInputStartEventData
                                {
                                    ToolName = string.IsNullOrWhiteSpace(toolState.ToolName) ? "tool" : toolState.ToolName,
                                    Title = ResolveUnifiedToolTitle(request, toolState.ToolName),
                                    ProviderExecuted = toolState.ProviderExecuted
                                },
                                lastTimestamp,
                                responseMetadata);
                            toolState.StartEmitted = true;
                        }

                        if (!string.IsNullOrEmpty(inputText))
                        {
                            toolState.InputBuilder.Append(inputText);
                            yield return CreateStreamEvent(
                                providerId,
                                toolCallId,
                                "tool-input-delta",
                                new AIToolInputDeltaEventData
                                {
                                    InputTextDelta = inputText
                                },
                                lastTimestamp,
                                responseMetadata);
                        }

                        break;
                    }

                case "tool.execution.done":
                case "agent.handoff.done":
                    {
                        var toolCallId = evt.GetString("tool_call_id")
                                         ?? evt.GetString("id")
                                         ?? Guid.NewGuid().ToString("n");
                        var toolName = evt.GetString("name") ?? string.Empty;
                        var toolState = GetOrCreateStreamingToolState(activeToolExecutions, activeToolExecutionOrder, toolCallId, toolName);

                        if (!string.IsNullOrWhiteSpace(toolName))
                        {
                            toolState.ToolName = toolName;
                            toolState.ProviderExecuted = IsProviderExecutedTool(toolName);
                        }

                        if (!toolState.StartEmitted)
                        {
                            yield return CreateStreamEvent(
                                providerId,
                                toolCallId,
                                "tool-input-start",
                                new AIToolInputStartEventData
                                {
                                    ToolName = string.IsNullOrWhiteSpace(toolState.ToolName) ? "tool" : toolState.ToolName,
                                    Title = ResolveUnifiedToolTitle(request, toolState.ToolName),
                                    ProviderExecuted = toolState.ProviderExecuted
                                },
                                lastTimestamp,
                                responseMetadata);
                            toolState.StartEmitted = true;
                        }

                        yield return CreateStreamEvent(
                            providerId,
                            toolCallId,
                            "tool-input-available",
                            new AIToolInputAvailableEventData
                            {
                                ToolName = string.IsNullOrWhiteSpace(toolState.ToolName) ? "tool" : toolState.ToolName,
                                Title = ResolveUnifiedToolTitle(request, toolState.ToolName),
                                ProviderExecuted = toolState.ProviderExecuted,
                                Input = DeserializeToolInput(toolState.InputBuilder.ToString())
                            },
                            lastTimestamp,
                            responseMetadata);

                        if (toolState.ProviderExecuted)
                        {
                            yield return CreateStreamEvent(
                                providerId,
                                toolCallId,
                                "tool-output-available",
                                CreateProviderToolOutputEventData(providerId, toolCallId, toolState.ToolName, preliminary: false),
                                lastTimestamp,
                                responseMetadata);
                        }

                        activeToolExecutions.Remove(toolCallId);
                        activeToolExecutionOrder.Remove(toolCallId);
                        break;
                    }

                case "conversation.response.done":
                    {
                        usage = ExtractUsage(evt.GetNode("usage"));
                        responseMetadata = EnrichMetadataWithGatewayCost(responseMetadata, usage, activeModel);

                        if (!string.IsNullOrWhiteSpace(evt.GetString("conversation_id")))
                            responseMetadata["mistral.conversation_id"] = evt.GetString("conversation_id");

                        foreach (var pendingToolCallId in activeToolExecutionOrder.ToList())
                        {
                            if (!activeToolExecutions.TryGetValue(pendingToolCallId, out var toolState))
                                continue;

                            yield return CreateStreamEvent(
                                providerId,
                                pendingToolCallId,
                                "tool-input-available",
                                new AIToolInputAvailableEventData
                                {
                                    ToolName = string.IsNullOrWhiteSpace(toolState.ToolName) ? "tool" : toolState.ToolName,
                                    Title = ResolveUnifiedToolTitle(request, toolState.ToolName),
                                    ProviderExecuted = toolState.ProviderExecuted,
                                    Input = DeserializeToolInput(toolState.InputBuilder.ToString())
                                },
                                lastTimestamp,
                                responseMetadata);

                            if (toolState.ProviderExecuted)
                            {
                                yield return CreateStreamEvent(
                                    providerId,
                                    pendingToolCallId,
                                    "tool-output-available",
                                    CreateProviderToolOutputEventData(providerId, pendingToolCallId, toolState.ToolName, preliminary: false),
                                    lastTimestamp,
                                    responseMetadata);
                            }
                        }

                        if (textStarted)
                        {
                            yield return CreateStreamEvent(
                                providerId,
                                responseEventId,
                                "text-end",
                                new AITextEndEventData(),
                                lastTimestamp,
                                responseMetadata);
                            textStarted = false;
                        }

                        activeToolExecutions.Clear();
                        activeToolExecutionOrder.Clear();

                        yield return CreateFinishStreamEvent(providerId, responseEventId, lastTimestamp, activeModel, usage, responseMetadata);
                        finishEmitted = true;
                        break;
                    }

                case "conversation.response.error":
                    {
                        if (textStarted)
                        {
                            yield return CreateStreamEvent(
                                providerId,
                                responseEventId,
                                "text-end",
                                new AITextEndEventData
                                {
                                    // ProviderMetadata = ToProviderMetadata(responseMetadata)
                                },
                                lastTimestamp,
                                responseMetadata);
                        }

                        yield return CreateStreamEvent(
                            providerId,
                            responseEventId,
                            "error",
                            new AIErrorEventData
                            {
                                ErrorText = $"Mistral stream error event: {evt.Payload.ToJsonString(MistralJsonSerializerOptions)}"
                            },
                            lastTimestamp,
                            responseMetadata);

                        yield break;
                    }
            }
        }

        if (!finishEmitted)
        {
            responseMetadata = EnrichMetadataWithGatewayCost(responseMetadata, usage, activeModel);

            foreach (var pendingToolCallId in activeToolExecutionOrder.ToList())
            {
                if (!activeToolExecutions.TryGetValue(pendingToolCallId, out var toolState))
                    continue;

                yield return CreateStreamEvent(
                    providerId,
                    pendingToolCallId,
                    "tool-input-available",
                    new AIToolInputAvailableEventData
                    {
                        ToolName = string.IsNullOrWhiteSpace(toolState.ToolName) ? "tool" : toolState.ToolName,
                        Title = ResolveUnifiedToolTitle(request, toolState.ToolName),
                        ProviderExecuted = toolState.ProviderExecuted,
                        Input = DeserializeToolInput(toolState.InputBuilder.ToString())
                    },
                    lastTimestamp,
                    responseMetadata);

                if (toolState.ProviderExecuted)
                {
                    yield return CreateStreamEvent(
                        providerId,
                        pendingToolCallId,
                        "tool-output-available",
                        CreateProviderToolOutputEventData(providerId, pendingToolCallId, toolState.ToolName, preliminary: false),
                        lastTimestamp,
                        responseMetadata);
                }
            }

            if (textStarted)
            {
                yield return CreateStreamEvent(
                    providerId,
                    responseEventId,
                    "text-end",
                    new AITextEndEventData(),
                    lastTimestamp,
                    responseMetadata);
            }

            yield return CreateFinishStreamEvent(providerId, responseEventId, lastTimestamp, activeModel, usage, responseMetadata);
        }
    }

    private MistralConversationRequest BuildUnifiedConversationRequest(
        AIRequest request,
        ConversationTarget target,
        MistralProviderMetadata? providerMetadata,
        bool stream)
    {
        var inputs = BuildUnifiedConversationInputs(request);
        var instructions = BuildUnifiedInstructions(request);
        var tools = BuildUnifiedConversationTools(request, providerMetadata);

        var completionArgs = new MistralConversationCompletionArgs
        {
            Temperature = request.Temperature,
            MaxTokens = request.MaxOutputTokens,
            TopP = request.TopP,
            RandomSeed = providerMetadata?.CompletionArgs?.TryGetValue("random_seed", out var o) == true
                ? o switch { int i => i, long l => (int)l, string s when int.TryParse(s, out var i) => i, JsonElement j when j.TryGetInt32(out var i) => i, _ => null }
                : null,
            FrequencyPenalty = providerMetadata?.CompletionArgs?.GetDouble("frequency_penalty"),
            PresencePenalty = providerMetadata?.CompletionArgs?.GetDouble("presence_penalty"),
            ReasoningEffort = providerMetadata?.CompletionArgs?.GetValueOrDefault("reasoning_effort")?.ToString(),
            ToolChoice = MistralExtensions.NormalizeUnifiedToolChoice(request.ToolChoice),
            ResponseFormat = request.ResponseFormat is null
                ? null
                : JsonSerializer.SerializeToNode(request.ResponseFormat, MistralJsonSerializerOptions)
        };

        return CreateConversationRequest(
            target,
            JsonSerializer.SerializeToNode(inputs.ToArray(), MistralJsonSerializerOptions) ?? new JsonArray(),
            instructions,
            completionArgs,
            ToToolArrayNode(tools),
            stream);
    }

    private string? BuildUnifiedInstructions(AIRequest request)
        => request.BuildMistralUnifiedInstructions();

    private List<object> BuildUnifiedConversationInputs(AIRequest request)
        => request.BuildMistralUnifiedConversationInputs();

    private IEnumerable<object> BuildUnifiedConversationEntries(AIInputItem item)
        => item.BuildMistralUnifiedConversationEntries();

    private object? ToMistralUnifiedContentPart(AIContentPart part)
        => part.ToMistralUnifiedContentPart();

    private object ToMistralUnifiedFileContentPart(AIFileContentPart filePart)
        => filePart.ToMistralUnifiedFileContentPart();

    private List<JsonNode> BuildUnifiedConversationTools(
        AIRequest request,
        MistralProviderMetadata? providerMetadata)
        => request.BuildMistralConversationTools(providerMetadata);

    private async Task<AIResponse> CreateUnifiedResponseAsync(
        AIRequest request,
        MistralConversationResponse response,
        ConversationTarget target,
        CancellationToken cancellationToken)
    {
        var outputItems = new List<AIOutputItem>();
        var sources = new List<Dictionary<string, object?>>();
        var files = new List<Dictionary<string, object?>>();
        var downloadErrors = new List<string>();

        foreach (var output in response.Outputs ?? [])
        {
            if (output is null)
                continue;

            var outputType = GetString(output, "type") ?? string.Empty;

            if (string.Equals(outputType, "message.output", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(outputType))
            {
                var content = new List<AIContentPart>();

                foreach (var part in EnumerateContentParts(output["content"]))
                {
                    switch (part.Type)
                    {
                        case "output_text":
                        case "text":
                            if (!string.IsNullOrEmpty(part.Text))
                            {
                                content.Add(new AITextContentPart
                                {
                                    Type = "text",
                                    Text = part.Text,
                                    Metadata = part.Raw is null
                                        ? null
                                        : new Dictionary<string, object?>
                                        {
                                            ["mistral.raw"] = part.Raw.DeepClone()
                                        }
                                });
                            }
                            break;

                        case "tool_reference":
                        case "document_url":
                            if (TryGetValidSourceUrl(part.Url, out var sourceUrl))
                            {
                                sources.Add(new Dictionary<string, object?>
                                {
                                    ["source_id"] = sourceUrl,
                                    ["url"] = sourceUrl,
                                    ["title"] = part.Title,
                                    ["type"] = part.Type
                                });
                            }
                            break;

                        case "tool_file":
                            {
                                var download = await TryDownloadConversationFileAsync(part.FileId, part.FileType, cancellationToken);
                                if (download.File is not null)
                                {
                                    content.Add(new AIFileContentPart
                                    {
                                        Type = "file",
                                        MediaType = download.File.MimeType,
                                        Filename = download.File.FileName ?? part.FileName,
                                        Data = ToDataUrl(download.File.Bytes, download.File.MimeType),
                                        Metadata = new Dictionary<string, object?>
                                        {
                                            ["mistral.file_id"] = download.File.FileId,
                                            ["mistral.file_name"] = download.File.FileName ?? part.FileName,
                                            ["mistral.file_type"] = download.File.MimeType,
                                            ["mistral.raw"] = part.Raw?.DeepClone()
                                        }
                                    });

                                    files.Add(new Dictionary<string, object?>
                                    {
                                        ["file_id"] = download.File.FileId,
                                        ["filename"] = download.File.FileName ?? part.FileName,
                                        ["media_type"] = download.File.MimeType,
                                        ["url"] = $"https://api.mistral.ai/v1/files/{download.File.FileId}"
                                    });
                                }
                                else if (!string.IsNullOrWhiteSpace(download.Error))
                                {
                                    downloadErrors.Add(download.Error);
                                }

                                break;
                            }
                    }
                }

                outputItems.Add(new AIOutputItem
                {
                    Type = "message",
                    Role = "assistant",
                    Content = content,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["mistral.output.type"] = outputType,
                        ["mistral.output.raw"] = output.DeepClone()
                    }
                });

                continue;
            }

            if (LooksLikeToolCallOutput(output))
            {
                outputItems.Add(new AIOutputItem
                {
                    Type = outputType,
                    Role = "assistant",
                    Content =
                    [
                        CreateUnifiedToolCallContentPart(output)
                    ],
                    Metadata = new Dictionary<string, object?>
                    {
                        ["mistral.output.raw"] = output.DeepClone()
                    }
                });
            }
        }

        var usage = ExtractUsage(response.Usage);

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = $"{GetIdentifier()}/{request.Model}",
            Status = "completed",
            Output = new AIOutput
            {
                Items = outputItems,
            },
            Usage = CreateUsageObject(usage)
        };
    }

    private static string NormalizeUnifiedRole(string? role)
        => MistralExtensions.NormalizeUnifiedRole(role);

    private static string? NormalizeUnifiedToolChoice(object? toolChoice)
        => MistralExtensions.NormalizeUnifiedToolChoice(toolChoice);

    private string FlattenSystemContent(List<AIContentPart>? content)
        => MistralExtensions.FlattenSystemContent(content);

    private static string SerializeUnifiedToolOutput(object output)
        => MistralExtensions.SerializeUnifiedToolOutput(output);

    private MistralProviderMetadata? GetUnifiedProviderMetadata(AIRequest request)
        => request.GetMistralProviderMetadata(GetIdentifier());

    private JsonNode? TryExtractRawMistralToolNode(Dictionary<string, object?>? metadata)
        => MistralExtensions.TryExtractRawMistralToolNode(metadata);

    private JsonNode? TryExtractRawMistralNode(Dictionary<string, object?>? metadata)
        => MistralExtensions.TryExtractRawMistralNode(metadata);

    private static JsonNode? TryConvertToJsonNode(object value)
        => MistralExtensions.TryConvertToJsonNode(value);

    private static string? TryNormalizeImageInput(AIFileContentPart filePart)
        => MistralExtensions.TryNormalizeImageInput(filePart);

    private static string? ResolveImageMediaType(AIFileContentPart filePart)
        => MistralExtensions.ResolveImageMediaType(filePart);

    private static string? GuessImageMediaType(string? filename)
        => MistralExtensions.GuessImageMediaType(filename);

    private static bool LooksLikeBase64(string value)
        => MistralExtensions.LooksLikeBase64(value);

    private static string ToDataUrl(byte[] bytes, string? mediaType)
        => MistralExtensions.ToDataUrl(bytes, mediaType);

    private AIToolCallContentPart CreateUnifiedToolCallContentPart(JsonNode output)
        => MistralExtensions.CreateUnifiedToolCallContentPart(output);

    private static bool LooksLikeToolCallOutput(JsonNode output)
        => MistralExtensions.LooksLikeToolCallOutput(output);

    private static object? ToUntypedObject(JsonNode? node)
        => MistralExtensions.ToUntypedObject(node);

    private static bool IsProviderExecutedTool(string? toolName)
        => MistralExtensions.IsProviderExecutedTool(toolName);

    private static UnifiedStreamingToolState GetOrCreateStreamingToolState(
        Dictionary<string, UnifiedStreamingToolState> activeToolExecutions,
        List<string> executionOrder,
        string toolCallId,
        string toolName)
    {
        if (activeToolExecutions.TryGetValue(toolCallId, out var existing))
            return existing;

        var created = new UnifiedStreamingToolState
        {
            ToolCallId = toolCallId,
            ToolName = toolName,
            ProviderExecuted = IsProviderExecutedTool(toolName)
        };

        activeToolExecutions[toolCallId] = created;
        executionOrder.Add(toolCallId);
        return created;
    }

    private static string? ResolveUnifiedToolTitle(AIRequest request, string? toolName)
        => string.IsNullOrWhiteSpace(toolName)
            ? null
            : request.Tools?.FirstOrDefault(tool => tool.Name == toolName)?.Title;

    private static Dictionary<string, object?> CreateUsageObject(MistralConversationUsage usage)
        => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt_tokens"] = usage.PromptTokens,
            ["completion_tokens"] = usage.CompletionTokens,
            ["total_tokens"] = usage.TotalTokens,
            ["input_tokens"] = usage.PromptTokens,
            ["output_tokens"] = usage.CompletionTokens
        };

    private Dictionary<string, object?> EnrichMetadataWithGatewayCost(
        Dictionary<string, object?> metadata,
        MistralConversationUsage? usage,
        string? modelId)
    {
        if (usage is null)
            return metadata;

        var pricing = ResolveCatalogPricing(modelId);
        if (pricing is null)
            return metadata;

        return ModelCostMetadataEnricher.AddCostFromUsage(CreateUsageObject(usage.Value), metadata, pricing);
    }

    private static AIStreamEvent CreateDataStreamEvent(
        string providerId,
        string type,
        object payload,
        Dictionary<string, string>? headers)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Timestamp = DateTimeOffset.UtcNow,
                Data = new AIDataEventData
                {
                    Data = payload
                }
            },
            Metadata = headers is null || headers.Count == 0
                ? null
                : new Dictionary<string, object?>
                {
                    ["unified.request.headers"] = headers.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)
                }
        };

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

    private static bool TryGetValidSourceUrl(string? url, out string? validUrl)
    {
        validUrl = null;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        var trimmedUrl = url.Trim();
        if (!Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
            return false;

        validUrl = trimmedUrl;
        return true;
    }

    private static AIStreamEvent CreateSourceUrlEvent(
        string providerId,
        string? eventId,
        string sourceUrl,
        MistralContentPart part,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => CreateStreamEvent(
            providerId,
            eventId,
            "source-url",
            new AISourceUrlEventData
            {
                SourceId = sourceUrl,
                Url = sourceUrl,
                Title = part.Title,
                Type = part.Type,
                ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
                {
                    [providerId] = new Dictionary<string, object>
                    {
                        ["type"] = part.Type,
                        ["title"] = part.Title ?? string.Empty
                    }
                }
            },
            timestamp,
            metadata);

    private static AIStreamEvent CreateFileStreamEvent(
        string providerId,
        string? eventId,
        MistralDownloadedFile file,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => CreateStreamEvent(
            providerId,
            eventId,
            "file",
            new
            {
                mediaType = file.MimeType,
                url = ToDataUrl(file.Bytes, file.MimeType),
                providerMetadata = new Dictionary<string, Dictionary<string, object>?>
                {
                    [providerId] = new Dictionary<string, object>
                    {
                        ["type"] = "tool_file",
                        ["file_id"] = file.FileId,
                        ["filename"] = file.FileName ?? string.Empty,
                        ["media_type"] = file.MimeType
                    }
                }
            },
            timestamp,
            metadata);

    private static AIStreamEvent CreateToolOutputErrorStreamEvent(
        string providerId,
        string? toolCallId,
        string errorText,
        bool providerExecuted,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => CreateStreamEvent(
            providerId,
            toolCallId,
            "tool-output-error",
            new AIToolOutputErrorEventData
            {
                ToolCallId = toolCallId,
                ErrorText = errorText,
                ProviderExecuted = providerExecuted
            },
            timestamp,
            metadata);

    private static AIToolOutputAvailableEventData CreateProviderToolOutputEventData(
        string providerId,
        string toolCallId,
        string? toolName,
        bool preliminary)
    {
        var resolvedToolName = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName;

        return new AIToolOutputAvailableEventData
        {
            ProviderExecuted = true,
            Preliminary = preliminary,
            Output = new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = $"Provider-side tool '{resolvedToolName}' completed. Mistral did not expose an explicit tool output payload in this conversations stream."
                    }
                ],
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    tool_name = resolvedToolName,
                    tool_use_id = toolCallId,
                    preliminary
                }, JsonSerializerOptions.Web)
            },
            ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                [providerId] = new Dictionary<string, object>
                {
                    ["type"] = "tool_result",
                    ["tool_name"] = resolvedToolName,
                    ["tool_use_id"] = toolCallId,
                    ["preliminary"] = preliminary
                }
            }
        };
    }

    private static AIStreamEvent CreateFinishStreamEvent(
        string providerId,
        string? eventId,
        DateTimeOffset timestamp,
        string? model,
        MistralConversationUsage? usage,
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
                    FinishReason = "stop",
                    Model = $"{providerId}/{model}",
                    CompletedAt = timestamp.ToUnixTimeSeconds(),
                    InputTokens = usage?.PromptTokens,
                    OutputTokens = usage?.CompletionTokens,
                    TotalTokens = usage?.TotalTokens,
                    MessageMetadata = ToMessageMetadata(metadata, model, timestamp, usage)
                }
            },
            Metadata = metadata
        };

    private static Dictionary<string, object>? ToMessageMetadata(
        Dictionary<string, object?>? metadata,
        string? model = null,
        DateTimeOffset? timestamp = null,
        MistralConversationUsage? usage = null)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (metadata is not null)
        {
            foreach (var item in metadata)
            {
                if (item.Value is not null)
                    result[item.Key] = item.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(model))
            result["model"] = model;

        if (timestamp is not null)
            result["timestamp"] = timestamp.Value;

        if (usage is not null)
        {
            result["usage"] = CreateUsageObject(usage.Value);
            result["inputTokens"] = usage.Value.PromptTokens;
            result["outputTokens"] = usage.Value.CompletionTokens;
            result["totalTokens"] = usage.Value.TotalTokens;
        }

        return result.Count == 0 ? null : result;
    }

    private static Dictionary<string, object>? ToProviderMetadata(Dictionary<string, object?>? metadata)
        => ToMessageMetadata(metadata);

    private sealed class UnifiedStreamingToolState
    {
        public string ToolCallId { get; init; } = string.Empty;

        public string ToolName { get; set; } = string.Empty;

        public StringBuilder InputBuilder { get; } = new();

        public bool StartEmitted { get; set; }

        public bool ProviderExecuted { get; set; }
    }

    private static List<JsonNode> ResolveProviderConversationTools(MistralProviderMetadata? metadata)
        => MistralExtensions.ResolveProviderConversationTools(metadata);


}
