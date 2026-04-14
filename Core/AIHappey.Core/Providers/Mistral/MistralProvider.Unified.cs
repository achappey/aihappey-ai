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
        var conversationRequest = BuildUnifiedConversationRequest(request, target, providerMetadata, stream: false);
        var response = await StartConversationAsync(conversationRequest, cancellationToken);

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
        var conversationRequest = BuildUnifiedConversationRequest(request, target, providerMetadata, stream: true);

        yield return CreateDataStreamEvent(
            providerId,
            "data-conversations.request",
            JsonSerializer.SerializeToElement(conversationRequest, MistralJsonSerializerOptions),
            request.Headers);

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

        await foreach (var evt in StartConversationStreamAsync(conversationRequest, cancellationToken))
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
                                && !string.IsNullOrWhiteSpace(part.Url))
                            {
                                yield return CreateSourceUrlEvent(providerId, responseEventId, part, lastTimestamp, responseMetadata);
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
            ToolChoice = NormalizeUnifiedToolChoice(request.ToolChoice),
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
    {
        var sections = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            sections.Add(request.Instructions.Trim());

        foreach (var item in request.Input?.Items ?? [])
        {
            if (!string.Equals(NormalizeUnifiedRole(item.Role), "system", StringComparison.Ordinal))
                continue;

            var text = FlattenSystemContent(item.Content);
            if (!string.IsNullOrWhiteSpace(text))
                sections.Add(text);
        }

        return sections.Count == 0 ? null : string.Join("\n\n", sections);
    }

    private List<object> BuildUnifiedConversationInputs(AIRequest request)
    {
        var inputs = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
        {
            inputs.Add(new Dictionary<string, object?>
            {
                ["type"] = "message.input",
                ["role"] = "user",
                ["content"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = request.Input.Text
                    }
                }
            });
        }

        foreach (var item in request.Input?.Items ?? [])
            inputs.AddRange(BuildUnifiedConversationEntries(item));

        return inputs;
    }

    private IEnumerable<object> BuildUnifiedConversationEntries(AIInputItem item)
    {
        var role = NormalizeUnifiedRole(item.Role);
        if (string.Equals(role, "system", StringComparison.Ordinal))
            yield break;

        var contentParts = new List<object>();

        foreach (var part in item.Content ?? [])
        {
            if (part is AIToolCallContentPart)
                continue;

            var mapped = ToMistralUnifiedContentPart(part);
            if (mapped is not null)
                contentParts.Add(mapped);
        }

        if (contentParts.Count > 0)
        {
            yield return new Dictionary<string, object?>
            {
                ["type"] = "message.input",
                ["role"] = role,
                ["content"] = contentParts
            };
        }

        foreach (var toolPart in item.Content?.OfType<AIToolCallContentPart>() ?? Enumerable.Empty<AIToolCallContentPart>())
        {
            if (toolPart.ProviderExecuted == true)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(toolPart.ToolCallId))
                continue;

            yield return new Dictionary<string, object?>
            {
                ["type"] = "function.call",
                ["tool_call_id"] = toolPart.ToolCallId,
                ["name"] = toolPart.ToolName ?? "tool",
                ["arguments"] = JsonSerializer.Serialize(toolPart.Input ?? new { }, MistralJsonSerializerOptions)
            };

            if (toolPart.Output is not null)
            {
                yield return new Dictionary<string, object?>
                {
                    ["type"] = "function.result",
                    ["tool_call_id"] = toolPart.ToolCallId,
                    ["result"] = SerializeUnifiedToolOutput(toolPart.Output)
                };
            }
        }
    }

    private object? ToMistralUnifiedContentPart(AIContentPart part)
    {
        if (TryExtractRawMistralNode(part.Metadata) is { } rawNode)
            return rawNode;

        return part switch
        {
            AITextContentPart textPart when !string.IsNullOrWhiteSpace(textPart.Text) => new
            {
                type = "text",
                text = textPart.Text
            },
            AIReasoningContentPart reasoningPart when !string.IsNullOrWhiteSpace(reasoningPart.Text) => new
            {
                type = "text",
                text = reasoningPart.Text
            },
            AIFileContentPart filePart => ToMistralUnifiedFileContentPart(filePart),
            null => null,
            _ => null
        };
    }

    private object ToMistralUnifiedFileContentPart(AIFileContentPart filePart)
    {
        var imageUrl = TryNormalizeImageInput(filePart);
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            return new
            {
                type = "image_url",
                image_url = imageUrl
            };
        }

        throw new NotSupportedException(
            "Mistral unified conversations only supports image file inputs unless metadata already contains a native Mistral content block.");
    }

    private List<JsonNode> BuildUnifiedConversationTools(
        AIRequest request,
        MistralProviderMetadata? providerMetadata)
    {
        var tools = new List<JsonNode>();

        foreach (var tool in request.Tools ?? Enumerable.Empty<AIToolDefinition>())
        {
            if (TryExtractRawMistralToolNode(tool.Metadata) is { } rawNode)
            {
                tools.Add(rawNode);
                continue;
            }

            AddSerializedToolNode(tools, new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = tool.InputSchema
                }
            });
        }

        tools.AddRange(ResolveProviderConversationTools(providerMetadata));
        return tools;
    }

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

        foreach (var output in response.Outputs ?? new JsonArray())
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
                            if (!string.IsNullOrWhiteSpace(part.Url))
                            {
                                sources.Add(new Dictionary<string, object?>
                                {
                                    ["source_id"] = part.Url,
                                    ["url"] = part.Url,
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
                    Content = new List<AIContentPart>
                    {
                        CreateUnifiedToolCallContentPart(output)
                    },
                    Metadata = new Dictionary<string, object?>
                    {
                        ["mistral.output.raw"] = output.DeepClone()
                    }
                });
            }
        }

        var usage = ExtractUsage(response.Usage);
        var primaryOutput = GetPrimaryMessageOutput(response);
        var normalizedModel = NormalizeReportedModel(GetString(primaryOutput, "model"), target);

        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["mistral.conversation_id"] = response.ConversationId,
            ["mistral.requested_model"] = request.Model,
            ["mistral.target_model"] = target.Model,
            ["mistral.target_agent_id"] = target.AgentId,
            ["mistral.outputs"] = response.Outputs?.DeepClone(),
            ["mistral.usage"] = response.Usage?.DeepClone(),
            ["mistral.sources"] = sources.Count == 0 ? null : JsonSerializer.SerializeToElement(sources, JsonSerializerOptions.Web),
            ["mistral.files"] = files.Count == 0 ? null : JsonSerializer.SerializeToElement(files, JsonSerializerOptions.Web),
            ["mistral.file_download_errors"] = downloadErrors.Count == 0 ? null : downloadErrors
        };

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = normalizedModel,
            Status = "completed",
            Output = new AIOutput
            {
                Items = outputItems,
                Metadata = new Dictionary<string, object?>
                {
                    ["mistral.sources"] = sources.Count == 0 ? null : JsonSerializer.SerializeToElement(sources, JsonSerializerOptions.Web),
                    ["mistral.files"] = files.Count == 0 ? null : JsonSerializer.SerializeToElement(files, JsonSerializerOptions.Web),
                    ["mistral.file_download_errors"] = downloadErrors.Count == 0 ? null : downloadErrors
                }
            },
            Usage = CreateUsageObject(usage),
            Metadata = metadata
        };
    }

    private static string NormalizeUnifiedRole(string? role)
        => string.IsNullOrWhiteSpace(role)
            ? "user"
            : role.Trim().ToLowerInvariant() switch
            {
                "tool" => "assistant",
                _ => role.Trim().ToLowerInvariant()
            };

    private static string? NormalizeUnifiedToolChoice(object? toolChoice)
    {
        if (toolChoice is null)
            return null;

        if (toolChoice is string text)
            return string.IsNullOrWhiteSpace(text) ? null : text;

        if (toolChoice is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.String)
                return json.GetString();

            if (json.ValueKind == JsonValueKind.Object
                && json.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String)
            {
                return typeElement.GetString();
            }
        }

        if (toolChoice is JsonNode node)
        {
            var type = node["type"];
            if (type is JsonValue value && value.TryGetValue<string>(out var typeText))
                return typeText;
        }

        try
        {
            var element = JsonSerializer.SerializeToElement(toolChoice, JsonSerializerOptions.Web);
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String)
            {
                return typeElement.GetString();
            }
        }
        catch
        {
        }

        return toolChoice.ToString();
    }

    private string FlattenSystemContent(List<AIContentPart>? content)
    {
        if (content is null || content.Count == 0)
            return string.Empty;

        var textParts = new List<string>();

        foreach (var part in content)
        {
            switch (part)
            {
                case AITextContentPart text when !string.IsNullOrWhiteSpace(text.Text):
                    textParts.Add(text.Text.Trim());
                    break;

                case AIReasoningContentPart reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
                    textParts.Add(reasoning.Text.Trim());
                    break;

                case AIFileContentPart:
                    throw new NotSupportedException("Mistral unified conversations does not support file content in system messages.");

                case AIToolCallContentPart:
                    throw new NotSupportedException("Mistral unified conversations does not support tool calls inside system messages.");
            }
        }

        return string.Join("\n\n", textParts);
    }

    private static string SerializeUnifiedToolOutput(object output)
    {
        if (output is string text)
            return text;

        return JsonSerializer.Serialize(output, MistralJsonSerializerOptions);
    }

    private MistralProviderMetadata? GetUnifiedProviderMetadata(AIRequest request)
    {
        if (request.Metadata is null
            || !request.Metadata.TryGetValue(GetIdentifier(), out var providerValue)
            || providerValue is null)
        {
            return null;
        }

        try
        {
            return providerValue switch
            {
                JsonElement json when json.ValueKind == JsonValueKind.Object => json.Deserialize<MistralProviderMetadata>(JsonSerializerOptions.Web),
                JsonNode node => JsonSerializer.Deserialize<MistralProviderMetadata>(node.ToJsonString(MistralJsonSerializerOptions), JsonSerializerOptions.Web),
                _ => JsonSerializer.Deserialize<MistralProviderMetadata>(JsonSerializer.Serialize(providerValue, JsonSerializerOptions.Web), JsonSerializerOptions.Web)
            };
        }
        catch
        {
            return null;
        }
    }

    private JsonNode? TryExtractRawMistralToolNode(Dictionary<string, object?>? metadata)
        => TryExtractRawMistralNode(metadata);

    private JsonNode? TryExtractRawMistralNode(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        foreach (var key in new[] { "mistral.raw", "mistral.content.raw", "mistral.content", "mistral.node" })
        {
            if (!metadata.TryGetValue(key, out var value) || value is null)
                continue;

            if (TryConvertToJsonNode(value) is { } node)
                return node;
        }

        return null;
    }

    private static JsonNode? TryConvertToJsonNode(object value)
    {
        try
        {
            return value switch
            {
                JsonNode node => node.DeepClone(),
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                string text when !string.IsNullOrWhiteSpace(text)
                    && (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')) => JsonNode.Parse(text),
                _ => JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web)
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? TryNormalizeImageInput(AIFileContentPart filePart)
    {
        var mediaType = ResolveImageMediaType(filePart);
        if (mediaType is null)
            return null;

        return filePart.Data switch
        {
            string text when text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) => text,
            string text when text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                              || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) => text,
            string text when LooksLikeBase64(text) => $"data:{mediaType};base64,{text}",
            byte[] bytes => ToDataUrl(bytes, mediaType),
            JsonElement element when element.ValueKind == JsonValueKind.String => NormalizeImageString(element.GetString(), mediaType),
            _ => null
        };

        static string? NormalizeImageString(string? text, string mediaType)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }

            return LooksLikeBase64(text) ? $"data:{mediaType};base64,{text}" : null;
        }
    }

    private static string? ResolveImageMediaType(AIFileContentPart filePart)
    {
        if (!string.IsNullOrWhiteSpace(filePart.MediaType)
            && filePart.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return filePart.MediaType;
        }

        var guessedFromFilename = GuessImageMediaType(filePart.Filename);
        if (!string.IsNullOrWhiteSpace(guessedFromFilename))
            return guessedFromFilename;

        if (filePart.Data is string text
            && text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var separatorIndex = text.IndexOf(';');
            if (separatorIndex > 5)
                return text.Substring(5, separatorIndex - 5);
        }

        return null;
    }

    private static string? GuessImageMediaType(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return null;

        return Path.GetExtension(filename).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".avif" => "image/avif",
            _ => null
        };
    }

    private static bool LooksLikeBase64(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Length < 32 || trimmed.Length % 4 != 0)
            return false;

        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch) || ch is '+' or '/' or '=')
                continue;

            return false;
        }

        return true;
    }

    private static string ToDataUrl(byte[] bytes, string? mediaType)
        => $"data:{mediaType ?? MediaTypeNames.Application.Octet};base64,{Convert.ToBase64String(bytes)}";

    private AIToolCallContentPart CreateUnifiedToolCallContentPart(JsonNode output)
    {
        var toolName = GetString(output, "name") ?? string.Empty;

        return new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = GetString(output, "tool_call_id") ?? GetString(output, "id") ?? Guid.NewGuid().ToString("n"),
            ToolName = toolName,
            Title = toolName,
            Input = DeserializeToolInput(ReadNodeAsString(output["arguments"])),
            Output = ToUntypedObject(output["result"]),
            State = GetString(output, "status") ?? GetString(output, "type"),
            ProviderExecuted = IsProviderExecutedTool(toolName),
            Metadata = new Dictionary<string, object?>
            {
                ["mistral.raw"] = output.DeepClone()
            }
        };
    }

    private static bool LooksLikeToolCallOutput(JsonNode output)
    {
        var type = GetString(output, "type") ?? string.Empty;
        return type.Contains("function", StringComparison.OrdinalIgnoreCase)
               || type.Contains("tool", StringComparison.OrdinalIgnoreCase)
               || output["tool_call_id"] is not null
               || output["arguments"] is not null;
    }

    private static object? ToUntypedObject(JsonNode? node)
    {
        if (node is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<object>(node.ToJsonString(MistralJsonSerializerOptions), MistralJsonSerializerOptions);
        }
        catch
        {
            return node.ToJsonString(MistralJsonSerializerOptions);
        }
    }

    private static bool IsProviderExecutedTool(string? toolName)
        => toolName is "code_interpreter" or "image_generation" or "web_search" or "web_search_premium";

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

    private static AIStreamEvent CreateSourceUrlEvent(
        string providerId,
        string? eventId,
        MistralContentPart part,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => CreateStreamEvent(
            providerId,
            eventId,
            "source-url",
            new AISourceUrlEventData
            {
                SourceId = part.Url ?? Guid.NewGuid().ToString("n"),
                Url = part.Url ?? string.Empty,
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
                Content = new List<ContentBlock>
                {
                    new TextContentBlock
                    {
                        Text = $"Provider-side tool '{resolvedToolName}' completed. Mistral did not expose an explicit tool output payload in this conversations stream."
                    }
                },
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
                    Model = model,
                    CompletedAt = timestamp.ToUnixTimeSeconds(),
                    InputTokens = usage?.PromptTokens,
                    OutputTokens = usage?.CompletionTokens,
                    TotalTokens = usage?.TotalTokens,
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
    {
        if (metadata?.Tools is not null)
        {
            var passthroughTools = new List<JsonNode>(metadata.Tools.Length);

            foreach (var tool in metadata.Tools)
            {
                if (TryCreateToolNode(tool) is { } node)
                    passthroughTools.Add(node);
            }

            return passthroughTools;
        }

        return [];
    }


}
