using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Mistral;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chatRequest.Model.Contains("voxtral"))
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        var conversationTarget = ResolveConversationTarget(chatRequest.Model);
        var metadata = chatRequest.GetProviderMetadata<MistralProviderMetadata>(GetIdentifier());
        var request = BuildChatConversationRequest(chatRequest, conversationTarget, metadata);

        string? streamId = null;
        string? toolStreamId = null;
        string toolInput = string.Empty;
        string streamingToolName = string.Empty;
        var runningToolExecutions = new Dictionary<string, StreamingToolExecutionState>(StringComparer.Ordinal);
        var runningToolExecutionOrder = new List<string>();
        bool textStarted = false;
        bool sawDone = false;

        string modelId = conversationTarget.ExposedModelId;
        int inputTokens = 0, outputTokens = 0, totalTokens = 0;

        await foreach (var evt in StartConversationStreamAsync(request, cancellationToken))
        {
            switch (evt.Type)
            {
                case "conversation.response.started":
                    break;

                case "message.output.delta":
                    {
                        modelId = NormalizeReportedModel(evt.GetString("model"), conversationTarget);
                        streamId ??= evt.GetString("id") ?? Guid.NewGuid().ToString("n");

                        foreach (var part in EnumerateContentParts(evt.GetNode("content")))
                        {
                            if (part.Type is "output_text" or "text")
                            {
                                if (!string.IsNullOrEmpty(part.Text))
                                {
                                    if (!textStarted)
                                    {
                                        yield return streamId.ToTextStartUIMessageStreamPart();
                                        textStarted = true;
                                    }

                                    yield return new TextDeltaUIMessageStreamPart
                                    {
                                        Id = streamId,
                                        Delta = part.Text
                                    };
                                }
                            }

                            if (part.Type == "tool_reference"
                                && !string.IsNullOrWhiteSpace(part.Url)
                                && part.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            {
                                yield return new SourceUIPart
                                {
                                    Url = part.Url,
                                    Title = string.IsNullOrWhiteSpace(part.Title) ? null : part.Title,
                                    SourceId = part.Url
                                };
                            }

                            if (part.Type == "tool_file")
                            {
                                var download = await TryDownloadConversationFileAsync(
                                    part.FileId,
                                    part.FileType,
                                    cancellationToken);

                                if (download.File is not null)
                                {
                                    yield return (download.File.Bytes.ToFileUIPart(download.File.MimeType));
                                    //yield return ;
                                }
                                else if (!string.IsNullOrWhiteSpace(download.Error))
                                {
                                    yield return download.Error.ToErrorUIPart();
                                }
                            }
                        }

                        break;
                    }

                case "conversation.response.done":
                    {
                        sawDone = true;

                        var usage = ExtractUsage(evt.GetNode("usage"));
                        inputTokens = usage.PromptTokens;
                        outputTokens = usage.CompletionTokens;
                        totalTokens = usage.TotalTokens;

                        if (textStarted && streamId is not null)
                        {
                            yield return new TextEndUIMessageStreamPart { Id = streamId };
                            textStarted = false;
                        }

                        foreach (var pendingToolExecutionId in runningToolExecutionOrder)
                        {
                            if (!runningToolExecutions.TryGetValue(pendingToolExecutionId, out var pendingToolExecution))
                                continue;

                            yield return new ToolCallPart
                            {
                                ToolCallId = pendingToolExecution.ToolCallId,
                                ToolName = pendingToolExecution.ToolName,
                                Title = ResolveToolTitle(chatRequest, pendingToolExecution.ToolName),
                                Input = DeserializeToolInput(pendingToolExecution.InputBuilder.ToString()),
                                ProviderExecuted = pendingToolExecution.ProviderExecuted
                            };

                            if (!pendingToolExecution.ProviderExecuted)
                            {
                                yield return new ToolApprovalRequestPart
                                {
                                    ToolCallId = pendingToolExecution.ToolCallId,
                                    ApprovalId = Guid.NewGuid().ToString()
                                };
                            }
                        }

                        runningToolExecutions.Clear();
                        runningToolExecutionOrder.Clear();

                        var resolvedModelId = modelId.ToModelId(GetIdentifier());
                        var finish = "stop".ToFinishUIPart(
                            resolvedModelId,
                            outputTokens,
                            inputTokens,
                            totalTokens,
                            chatRequest.Temperature,
                            reasoningTokens: null,
                            extraMetadata: null);

                        yield return ModelCostMetadataEnricher.AddCost(finish, ResolveCatalogPricing(resolvedModelId));

                        break;
                    }

                case "conversation.response.error":
                    {
                        if (textStarted && streamId is not null)
                        {
                            yield return new TextEndUIMessageStreamPart { Id = streamId };
                            textStarted = false;
                        }

                        yield return $"Mistral stream error event: {evt.Payload.ToJsonString(MistralJsonSerializerOptions)}"
                            .ToErrorUIPart();
                        yield break;
                    }

                case "tool.execution.delta":
                case "function.call.delta":
                    {
                        var toolDeltaId = evt.GetString("tool_call_id")
                            ?? evt.GetString("id") ?? Guid.NewGuid().ToString("n");
                        var argStreamDelta = ReadNodeAsString(evt.GetNode("arguments"));
                        var toolDeltaName = evt.GetString("name") ?? string.Empty;

                        if (!runningToolExecutions.TryGetValue(toolDeltaId, out var toolExecutionState))
                        {
                            toolExecutionState = new StreamingToolExecutionState
                            {
                                ToolCallId = toolDeltaId,
                                ToolName = toolDeltaName,
                                ProviderExecuted = IsProviderExecutedTool(toolDeltaName)
                            };

                            runningToolExecutions[toolDeltaId] = toolExecutionState;
                            runningToolExecutionOrder.Add(toolDeltaId);
                        }
                        else if (!string.IsNullOrWhiteSpace(toolDeltaName))
                        {
                            toolExecutionState.ToolName = toolDeltaName;
                            toolExecutionState.ProviderExecuted = IsProviderExecutedTool(toolDeltaName);
                        }

                        if (!toolExecutionState.StreamingStarted)
                        {
                            yield return new ToolCallStreamingStartPart
                            {
                                ToolCallId = toolDeltaId,
                                ToolName = toolExecutionState.ToolName,
                                Title = ResolveToolTitle(chatRequest, toolExecutionState.ToolName),
                                ProviderExecuted = toolExecutionState.ProviderExecuted
                            };

                            toolExecutionState.StreamingStarted = true;
                        }

                        if (!string.IsNullOrEmpty(argStreamDelta))
                        {
                            toolExecutionState.InputBuilder.Append(argStreamDelta);
                            yield return new ToolCallDeltaPart
                            {
                                ToolCallId = toolDeltaId,
                                InputTextDelta = argStreamDelta
                            };
                        }

                        break;
                    }

                case "tool.execution.started":
                    {
                        var toolId = evt.GetString("id") ?? Guid.NewGuid().ToString("n");
                        var toolName = evt.GetString("name") ?? string.Empty;
                        var argStream = ReadNodeAsString(evt.GetNode("arguments"));

                        switch (toolName)
                        {
                            default:
                                if (!runningToolExecutions.TryGetValue(toolId, out var toolExecutionState))
                                {
                                    toolExecutionState = new StreamingToolExecutionState
                                    {
                                        ToolCallId = toolId
                                    };

                                    runningToolExecutions[toolId] = toolExecutionState;
                                    runningToolExecutionOrder.Add(toolId);
                                }

                                toolExecutionState.ToolName = toolName;
                                toolExecutionState.ProviderExecuted = IsProviderExecutedTool(toolName);

                                if (!toolExecutionState.StreamingStarted)
                                {
                                    yield return new ToolCallStreamingStartPart
                                    {
                                        ToolCallId = toolId,
                                        ToolName = toolName,
                                        Title = ResolveToolTitle(chatRequest, toolName),
                                        ProviderExecuted = toolExecutionState.ProviderExecuted
                                    };

                                    toolExecutionState.StreamingStarted = true;
                                }

                                if (!string.IsNullOrEmpty(argStream))
                                {
                                    toolExecutionState.InputBuilder.Append(argStream);

                                    yield return new ToolCallDeltaPart
                                    {
                                        ToolCallId = toolId,
                                        InputTextDelta = argStream
                                    };
                                }


                                break;
                        }

                        break;
                    }

                case "tool.execution.done":
                    {
                        var toolCallId = evt.GetString("id") ?? Guid.NewGuid().ToString("n");
                        var toolDoneName = evt.GetString("name") ?? string.Empty;

                        if (!runningToolExecutions.TryGetValue(toolCallId, out var toolExecutionState))
                        {
                            toolExecutionState = new StreamingToolExecutionState
                            {
                                ToolCallId = toolCallId,
                                ToolName = toolDoneName,
                                ProviderExecuted = IsProviderExecutedTool(toolDoneName)
                            };
                        }
                        else
                        {
                            runningToolExecutions.Remove(toolCallId);
                            runningToolExecutionOrder.Remove(toolCallId);
                        }

                        if (string.IsNullOrWhiteSpace(toolExecutionState.ToolName))
                            toolExecutionState.ToolName = toolDoneName;

                        toolExecutionState.ProviderExecuted = IsProviderExecutedTool(toolExecutionState.ToolName);

                        yield return new ToolCallPart
                        {
                            ToolCallId = toolCallId,
                            ToolName = toolExecutionState.ToolName,
                            Title = ResolveToolTitle(chatRequest, toolExecutionState.ToolName),
                            Input = DeserializeToolInput(toolExecutionState.InputBuilder.ToString()),
                            ProviderExecuted = toolExecutionState.ProviderExecuted
                        };

                        if (!toolExecutionState.ProviderExecuted)
                        {
                            yield return new ToolApprovalRequestPart
                            {
                                ToolCallId = toolCallId,
                                ApprovalId = Guid.NewGuid().ToString()
                            };
                        }

                        yield return new ToolOutputAvailablePart
                        {
                            ToolCallId = toolCallId,
                            Output = new { },
                            ProviderExecuted = toolExecutionState.ProviderExecuted
                        };

                        break;
                    }

                case "agent.handoff.started":
                case "agent.handoff.done":

                    {
                        var deltaId = evt.GetString("tool_call_id")
                            ?? evt.GetString("id")
                            ?? Guid.NewGuid().ToString("n");
                        var argDelta = ReadNodeAsString(evt.GetNode("arguments"));
                        var argDeltaName = evt.GetString("name") ?? streamingToolName;

                        if (toolStreamId is null)
                        {
                            toolStreamId = deltaId;
                            streamingToolName = argDeltaName;

                            yield return new ToolCallStreamingStartPart
                            {
                                ToolCallId = deltaId,
                                ToolName = argDeltaName,
                                Title = ResolveToolTitle(chatRequest, argDeltaName),
                                ProviderExecuted = false
                            };
                        }
                        else if (!string.Equals(toolStreamId, deltaId, StringComparison.Ordinal))
                        {
                            yield return new ToolCallPart
                            {
                                ToolCallId = toolStreamId,
                                ToolName = streamingToolName,
                                Title = ResolveToolTitle(chatRequest, streamingToolName),
                                Input = DeserializeToolInput(toolInput),
                                ProviderExecuted = false
                            };

                            yield return new ToolApprovalRequestPart
                            {
                                ToolCallId = toolStreamId,
                                ApprovalId = Guid.NewGuid().ToString()
                            };

                            toolStreamId = deltaId;
                            streamingToolName = argDeltaName;
                            toolInput = string.Empty;

                            yield return new ToolCallStreamingStartPart
                            {
                                ToolCallId = deltaId,
                                ToolName = argDeltaName,
                                Title = ResolveToolTitle(chatRequest, argDeltaName),
                                ProviderExecuted = false
                            };
                        }

                        if (!string.IsNullOrEmpty(argDelta))
                        {
                            toolInput += argDelta;
                            yield return new ToolCallDeltaPart
                            {
                                ToolCallId = deltaId,
                                InputTextDelta = argDelta
                            };
                        }

                        break;
                    }

                default:
                    break;
            }
        }

        // Safety net if stream ended without explicit done
        if (!sawDone)
        {
            if (textStarted && streamId is not null)
                yield return new TextEndUIMessageStreamPart { Id = streamId };

            var resolvedModelId = modelId.ToModelId(GetIdentifier());
            var finish = "stop".ToFinishUIPart(
                resolvedModelId,
                outputTokens,
                inputTokens,
                inputTokens + outputTokens,
                chatRequest.Temperature,
                reasoningTokens: null,
                extraMetadata: null
            );

            yield return ModelCostMetadataEnricher.AddCost(finish, ResolveCatalogPricing(resolvedModelId));
        }
    }

    private ModelPricing? ResolveCatalogPricing(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var pricing = GetIdentifier().GetPricing();
        if (pricing == null || pricing.Count == 0)
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
            if (candidate != null && pricing.TryGetValue(candidate, out var modelPricing))
                return modelPricing;
        }

        return null;
    }

    private MistralConversationRequest BuildChatConversationRequest(
        ChatRequest chatRequest,
        ConversationTarget conversationTarget,
        MistralProviderMetadata? metadata)
    {
        var inputs = BuildChatConversationInputs(chatRequest);
        var tools = BuildChatConversationTools(chatRequest, metadata);
        var instructions = string.Join(
            "\n\n",
            chatRequest.Messages
                .Where(m => m.Role == Role.system)
                .SelectMany(m => m.Parts.OfType<TextUIPart>().Select(p => p.Text)));

        var completionArgs = new MistralConversationCompletionArgs
        {
            Temperature = chatRequest.Temperature,
            MaxTokens = chatRequest.MaxOutputTokens,
            TopP = chatRequest.TopP,
            ToolChoice = chatRequest.ToolChoice,
            ResponseFormat = chatRequest.ResponseFormat is null
                ? null
                : JsonSerializer.SerializeToNode(chatRequest.ResponseFormat, MistralJsonSerializerOptions)
        };

        return CreateConversationRequest(
            conversationTarget,
            JsonSerializer.SerializeToNode(inputs.ToArray(), MistralJsonSerializerOptions) ?? new JsonArray(),
            instructions,
            completionArgs,
            ToToolArrayNode(tools),
            stream: true);
    }

    private static List<object> BuildChatConversationInputs(ChatRequest chatRequest)
        => chatRequest.Messages
            .Where(m => m.Role != Role.system)
            .SelectMany(BuildChatMessageEntries)
            .ToList();

    private static IEnumerable<object> BuildChatMessageEntries(UIMessage message)
    {
        if (message.Role == Role.user)
        {
            yield return new Dictionary<string, object?>
            {
                ["type"] = "message.input",
                ["role"] = "user",
                ["content"] = message.Parts
                    .Select(ToMistralChatContentPart)
                    .OfType<object>()
                    .ToList()
            };

            yield break;
        }

        if (message.Role != Role.assistant)
            yield break;

        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case TextUIPart or FileUIPart:
                    var contentPart = ToMistralChatContentPart(part);
                    if (contentPart is not null)
                    {
                        yield return new Dictionary<string, object?>
                        {
                            ["type"] = "message.input",
                            ["role"] = "assistant",
                            ["content"] = new[] { contentPart }
                        };
                    }

                    break;

                case ToolInvocationPart tool when tool.ProviderExecuted != true:
                    var toolName = tool.GetToolName();
                    yield return new Dictionary<string, object?>
                    {
                        ["type"] = "function.call",
                        ["tool_call_id"] = tool.ToolCallId,
                        ["name"] = toolName,
                        ["arguments"] = JsonSerializer.Serialize(tool.Input, MistralJsonSerializerOptions)
                    };

                    if (tool.Output is not null)
                    {
                        yield return new Dictionary<string, object?>
                        {
                            ["type"] = "function.result",
                            ["tool_call_id"] = tool.ToolCallId,
                            ["result"] = tool.Output is string outputText
                                ? outputText
                                : JsonSerializer.Serialize(tool.Output, MistralJsonSerializerOptions)
                        };
                    }

                    break;
            }
        }
    }

    private static object? ToMistralChatContentPart(UIMessagePart part)
    {
        if (part is TextUIPart textPart)
            return new { type = "text", text = textPart.Text };

        if (part is FileUIPart filePart && filePart.IsImage())
            return new { type = "image_url", image_url = filePart.Url.ToDataUrl(filePart.MediaType) };

        return null;
    }

    private static List<JsonNode> BuildChatConversationTools(
        ChatRequest chatRequest,
        MistralProviderMetadata? metadata)
    {
        var tools = chatRequest.Tools?
            .Select(tool => JsonSerializer.SerializeToNode(new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = tool.InputSchema
                }
            }, MistralJsonSerializerOptions))
            .OfType<JsonNode>()
            .ToList() ?? [];

        tools.AddRange(ResolveProviderConversationTools(metadata));

        return tools;
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

    private static JsonNode? ToToolArrayNode(IEnumerable<JsonNode> tools)
    {
        var array = new JsonArray();

        foreach (var tool in tools)
        {
            array.Add(tool.DeepClone());
        }

        return array.Count == 0 ? null : array;
    }

    private static void AddSerializedToolNode(List<JsonNode> tools, object? tool)
    {
        if (tool is null)
            return;

        var node = JsonSerializer.SerializeToNode(tool, MistralJsonSerializerOptions);
        if (node is not null)
            tools.Add(node);
    }

    private static JsonNode? TryCreateToolNode(JsonElement tool)
    {
        if (tool.ValueKind != JsonValueKind.Object)
            return null;

        if (!tool.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(typeElement.GetString()))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(tool.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ResolveToolTitle(ChatRequest chatRequest, string? toolName)
        => string.IsNullOrWhiteSpace(toolName)
            ? null
            : chatRequest.Tools?.FirstOrDefault(tool => tool.Name == toolName)?.Title;

    private static bool IsProviderExecutedTool(string? toolName)
        => toolName is "code_interpreter" or "image_generation" or "web_search" or "web_search_premium";

    private static object DeserializeToolInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new { };

        try
        {
            return JsonSerializer.Deserialize<object>(input, MistralJsonSerializerOptions) ?? new { };
        }
        catch (JsonException)
        {
            return input;
        }
    }

    private sealed class StreamingToolExecutionState
    {
        public string ToolCallId { get; init; } = default!;

        public string ToolName { get; set; } = string.Empty;

        public StringBuilder InputBuilder { get; } = new();

        public bool StreamingStarted { get; set; }

        public bool ProviderExecuted { get; set; }
    }
}
