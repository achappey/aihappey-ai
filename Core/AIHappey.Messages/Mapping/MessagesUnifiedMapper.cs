using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Messages.Mapping;

public static class MessagesUnifiedMapper
{
    private static readonly JsonSerializerOptions Json = MessagesJson.Default;

    private static readonly HashSet<string> MappedRequestFields =
    [
        "model",
        "max_tokens",
        "messages",
        "cache_control",
        "container",
        "inference_geo",
        "metadata",
        "output_config",
        "service_tier",
        "stop_sequences",
        "stream",
        "system",
        "temperature",
        "thinking",
        "tool_choice",
        "tools",
        "top_k",
        "top_p"
    ];

    public static AIRequest ToUnifiedRequest(this MessagesRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        return new AIRequest
        {
            ProviderId = providerId,
            Model = request.Model,
            Instructions = FlattenContentText(request.System),
            Input = new AIInput
            {
                Items = request.Messages.Select(ToUnifiedInputItem).ToList(),
                Metadata = new Dictionary<string, object?>
                {
                    ["messages.input.raw"] = request.Messages.Count == 0
                        ? null
                        : JsonSerializer.SerializeToElement(request.Messages, Json)
                }
            },
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxTokens,
            Stream = request.Stream,
            ParallelToolCalls = request.ToolChoice?.DisableParallelToolUse is bool disable ? !disable : null,
            ToolChoice = request.ToolChoice is null ? null : JsonSerializer.SerializeToElement(request.ToolChoice, Json),
            Tools = request.Tools?.Select(ToUnifiedTool).ToList(),
            Metadata = BuildUnifiedRequestMetadata(request)
        };
    }

    public static MessagesRequest ToMessagesRequest(this AIRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = request.Metadata ?? new Dictionary<string, object?>();
        var system = ExtractObject<MessagesContent>(metadata, "messages.request.system")
                     ?? (!string.IsNullOrWhiteSpace(request.Instructions) ? new MessagesContent(request.Instructions) : null);

        var metadataObj = JsonSerializer.Deserialize<MessagesRequestMetadata>(
            JsonSerializer.Serialize(metadata, JsonSerializerOptions.Web)
        );

        return new MessagesRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            Messages = ToMessageParams(request.Input?.Items ?? []).ToList(),
            CacheControl = ExtractObject<CacheControlEphemeral>(metadata, "messages.request.cache_control"),
            Container = ExtractValue<string>(metadata, "messages.request.container"),
            InferenceGeo = ExtractValue<string>(metadata, "messages.request.inference_geo"),
            Metadata = metadataObj,
            OutputConfig = ExtractObject<MessagesOutputConfig>(metadata, "messages.request.output_config"),
            ServiceTier = ExtractValue<string>(metadata, "messages.request.service_tier"),
            StopSequences = ExtractObject<List<string>>(metadata, "messages.request.stop_sequences"),
            Stream = request.Stream,
            System = system,
            Temperature = request.Temperature,
            Thinking = ExtractObject<MessagesThinkingConfig>(metadata, "messages.request.thinking"),
            ToolChoice = request.ToolChoice == null ? new MessageToolChoice()
            {
                Type = "auto",
                DisableParallelToolUse = false

            } : new MessageToolChoice()
            {
                Type = request.ToolChoice?.ToString()!,
                DisableParallelToolUse = false
            },
            Tools = request.Tools?.Select(ToMessageTool).ToList(),
            TopK = ExtractValue<int?>(metadata, "messages.request.top_k"),
            TopP = request.TopP,
            AdditionalProperties = ExtractObject<Dictionary<string, JsonElement>>(metadata, "messages.request.unmapped")
        };
    }

    public static AIResponse ToUnifiedResponse(this MessagesResponse response, string providerId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var outputItems = ToUnifiedOutputItems(response).ToList();

        return new AIResponse
        {
            ProviderId = providerId,
            Model = response.Model,
            Status = ToUnifiedStatus(response.StopReason),
            Usage = response.Usage,
            Output = outputItems.Count == 0 ? null : new AIOutput { Items = outputItems },
            Metadata = BuildUnifiedResponseMetadata(response)
        };
    }

    public static MessagesResponse ToMessagesResponse(this AIResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var metadata = response.Metadata ?? new Dictionary<string, object?>();
        var role = ExtractValue<string>(metadata, "messages.response.role") ?? "assistant";

        return new MessagesResponse
        {
            Id = ExtractValue<string>(metadata, "messages.response.id") ?? $"msg_{Guid.NewGuid():N}",
            Container = ExtractObject<MessagesContainer>(metadata, "messages.response.container"),
            Content = ToMessageContentBlocks(response.Output).ToList(),
            Model = response.Model,
            Role = role,
            StopDetails = ExtractObject<MessagesStopDetails>(metadata, "messages.response.stop_details"),
            StopReason = ExtractValue<string>(metadata, "messages.response.stop_reason") ?? ToMessagesStopReason(response.Status),
            StopSequence = ExtractValue<string>(metadata, "messages.response.stop_sequence"),
            Type = ExtractValue<string>(metadata, "messages.response.type") ?? "message",
            Usage = ExtractObject<MessagesUsage>(metadata, "messages.response.usage") ?? DeserializeFromObject<MessagesUsage>(response.Usage),
            AdditionalProperties = ExtractObject<Dictionary<string, JsonElement>>(metadata, "messages.response.unmapped")
        };
    }

    public static IEnumerable<AIStreamEvent> ToUnifiedStreamEvents(
        this MessageStreamPart part,
        string providerId,
        MessagesStreamMappingState? state = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        state ??= new MessagesStreamMappingState();

        foreach (var envelope in ToUnifiedEnvelopes(part, state))
        {
            yield return new AIStreamEvent
            {
                ProviderId = providerId,
                Event = envelope,
                Metadata = new Dictionary<string, object?>
                {
                    ["messages.stream.type"] = part.Type,
                    ["messages.stream.raw"] = JsonSerializer.SerializeToElement(part, Json)
                }
            };
        }

        yield return new AIStreamEvent
        {
            ProviderId = providerId,
            Event = CreateDataEnvelope(
                part.Type,
                JsonSerializer.SerializeToElement(part, part.GetType(), Json)),
            Metadata = new Dictionary<string, object?>
            {
                ["messages.stream.type"] = part.Type,
                ["messages.stream.raw"] = JsonSerializer.SerializeToElement(part, Json)
            }
        };
    }

    private static Dictionary<string, object?> BuildUnifiedRequestMetadata(MessagesRequest request)
    {
        var raw = JsonSerializer.SerializeToElement(request, Json);
        var metadata = new Dictionary<string, object?>
        {
            ["messages.request.raw"] = raw,
            ["messages.request.cache_control"] = request.CacheControl,
            ["messages.request.container"] = request.Container,
            ["messages.request.inference_geo"] = request.InferenceGeo,
            ["messages.request.metadata"] = request.Metadata,
            ["messages.request.output_config"] = request.OutputConfig,
            ["messages.request.service_tier"] = request.ServiceTier,
            ["messages.request.stop_sequences"] = request.StopSequences,
            ["messages.request.system"] = request.System,
            ["messages.request.thinking"] = request.Thinking,
            ["messages.request.tool_choice"] = request.ToolChoice,
            ["messages.request.top_k"] = request.TopK,
            ["messages.request.top_p"] = request.TopP
        };

        var unmapped = new Dictionary<string, JsonElement>();
        foreach (var prop in raw.EnumerateObject())
        {
            metadata[$"messages.request.{prop.Name}"] = prop.Value.Clone();
            if (!MappedRequestFields.Contains(prop.Name))
                unmapped[prop.Name] = prop.Value.Clone();
        }

        metadata["messages.request.unmapped"] = JsonSerializer.SerializeToElement(unmapped, Json);
        return metadata;
    }

    private static Dictionary<string, object?> BuildUnifiedResponseMetadata(MessagesResponse response)
    {
        var raw = JsonSerializer.SerializeToElement(response, Json);
        var metadata = new Dictionary<string, object?>
        {
            ["messages.response.raw"] = raw,
            ["messages.response.id"] = response.Id,
            ["messages.response.container"] = response.Container,
            ["messages.response.role"] = response.Role,
            ["messages.response.stop_details"] = response.StopDetails,
            ["messages.response.stop_reason"] = response.StopReason,
            ["messages.response.stop_sequence"] = response.StopSequence,
            ["messages.response.type"] = response.Type,
            ["messages.response.usage"] = response.Usage
        };

        var unmapped = new Dictionary<string, JsonElement>();
        foreach (var prop in raw.EnumerateObject())
        {
            metadata[$"messages.response.{prop.Name}"] = prop.Value.Clone();

            if (prop.Name is not "id"
                and not "container"
                and not "content"
                and not "model"
                and not "role"
                and not "stop_details"
                and not "stop_reason"
                and not "stop_sequence"
                and not "type"
                and not "usage")
            {
                unmapped[prop.Name] = prop.Value.Clone();
            }
        }

        metadata["messages.response.unmapped"] = JsonSerializer.SerializeToElement(unmapped, Json);
        return metadata;
    }

    private static AIInputItem ToUnifiedInputItem(MessageParam message)
        => new()
        {
            Type = "message",
            Role = message.Role,
            Content = ToUnifiedContentParts(message.Content).ToList(),
            Metadata = new Dictionary<string, object?>
            {
                ["messages.message.raw"] = JsonSerializer.SerializeToElement(message, Json)
            }
        };

    private static IEnumerable<MessageParam> ToMessageParams(IEnumerable<AIInputItem> items)
    {
        var yielded = new List<MessageParam>();
        var pendingAssistantBlocks = new List<MessageContentBlock>();
        string pendingAssistantRole = "assistant";

        void FlushAssistant()
        {
            if (pendingAssistantBlocks.Count == 0)
                return;

            yielded.Add(new MessageParam
            {
                Role = pendingAssistantRole,
                Content = CreateMessagesContentFromBlocks([.. pendingAssistantBlocks])
            });

            pendingAssistantBlocks.Clear();
        }

        foreach (var item in items)
        {
            pendingAssistantRole = NormalizeRole(item.Role);

            foreach (var part in item.Content ?? [])
            {
                if (part is AIToolCallContentPart toolPart)
                {
                    foreach (var (assistantBlock, userBlock) in ToMessageToolBlocks(toolPart))
                    {
                        if (assistantBlock is not null)
                        {
                            FlushAssistant();
                            yielded.Add(new MessageParam
                            {
                                Role = "assistant",
                                Content = CreateMessagesContentFromBlocks([assistantBlock])
                            });
                        }

                        if (userBlock is not null)
                        {
                            yielded.Add(new MessageParam
                            {
                                Role = "user",
                                Content = CreateMessagesContentFromBlocks([userBlock])
                            });
                        }
                    }

                    continue;
                }

                var raw = ExtractRawBlock(part.Metadata);
                if (raw is not null)
                {
                    pendingAssistantBlocks.Add(raw);
                    continue;
                }

                switch (part)
                {
                    case AITextContentPart text:
                        pendingAssistantBlocks.Add(new MessageContentBlock { Type = "text", Text = text.Text });
                        break;
                    case AIReasoningContentPart reasoning:
                        pendingAssistantBlocks.Add(new MessageContentBlock { Type = "thinking", Thinking = reasoning.Text });
                        break;
                    case AIFileContentPart file:
                        pendingAssistantBlocks.Add(ToMessageFileBlock(file));
                        break;
                }
            }
        }

        FlushAssistant();

        foreach (var message in yielded)
            yield return message;
    }

    private static IEnumerable<AIOutputItem> ToUnifiedOutputItems(MessagesResponse response)
    {
        var messageContent = new List<AIContentPart>();

        foreach (var block in response.Content ?? [])
        {
            if (TryCreateUnifiedToolCallPart(block, out var toolPart))
            {
                yield return new AIOutputItem
                {
                    Type = "message",
                    Role = response.Role ?? "assistant",
                    Content = [toolPart],
                    Metadata = CreateBlockMetadata(block)
                };

                foreach (var sourceItem in ToSourceOutputItems(block))
                    yield return sourceItem;

                continue;
            }

            switch (block.Type)
            {
                case "text":
                    messageContent.Add(new AITextContentPart
                    {
                        Type = "text",
                        Text = block.Text ?? string.Empty,
                        Metadata = CreateBlockMetadata(block)
                    });
                    break;
                case "thinking":
                case "redacted_thinking":
                    messageContent.Add(new AIReasoningContentPart
                    {
                        Type = "reasoning",
                        Text = block.Thinking ?? block.Data,
                        Metadata = CreateBlockMetadata(block)
                    });
                    break;
                case "image":
                case "document":
                case "container_upload":
                    messageContent.Add(ToUnifiedFilePart(block));
                    break;
                default:
                    yield return new AIOutputItem
                    {
                        Type = block.Type,
                        Role = response.Role,
                        Content = ToUnifiedToolLikeContent(block),
                        Metadata = CreateBlockMetadata(block)
                    };
                    break;
            }

            foreach (var sourceItem in ToSourceOutputItems(block))
                yield return sourceItem;
        }

        if (messageContent.Count > 0)
        {
            yield return new AIOutputItem
            {
                Type = "message",
                Role = response.Role ?? "assistant",
                Content = messageContent,
                Metadata = new Dictionary<string, object?>
                {
                    ["messages.output.role"] = response.Role,
                    ["messages.output.raw_content"] = JsonSerializer.SerializeToElement(response.Content, Json)
                }
            };
        }
    }

    private static IEnumerable<MessageContentBlock> ToMessageContentBlocks(AIOutput? output)
    {
        foreach (var item in output?.Items ?? [])
        {
            if (string.Equals(item.Type, "source-url", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
            {
                var rawBlock = ExtractRawBlock(item.Metadata);
                if (rawBlock is not null)
                {
                    yield return rawBlock;
                    continue;
                }
            }

            foreach (var part in item.Content ?? [])
            {
                if (part is AIToolCallContentPart toolPart)
                {
                    foreach (var (assistantBlock, userBlock) in ToMessageToolBlocks(toolPart))
                    {
                        if (assistantBlock is not null)
                            yield return assistantBlock;

                        if (userBlock is not null)
                            yield return userBlock;
                    }

                    continue;
                }

                var rawBlock = ExtractRawBlock(part.Metadata);
                if (rawBlock is not null)
                {
                    yield return rawBlock;
                    continue;
                }

                switch (part)
                {
                    case AITextContentPart text:
                        yield return new MessageContentBlock { Type = "text", Text = text.Text };
                        break;
                    case AIReasoningContentPart reasoning:
                        yield return new MessageContentBlock { Type = "thinking", Thinking = reasoning.Text };
                        break;
                    case AIFileContentPart file:
                        yield return ToMessageFileBlock(file);
                        break;
                }
            }
        }
    }

    private static IEnumerable<AIContentPart> ToUnifiedContentParts(MessagesContent content)
    {
        if (content.IsText && !string.IsNullOrWhiteSpace(content.Text))
        {
            yield return new AITextContentPart
            {
                Type = "text",
                Text = content.Text!,
                Metadata = new Dictionary<string, object?>
                {
                    ["messages.content.kind"] = "text"
                }
            };
            yield break;
        }

        foreach (var block in content.Blocks ?? [])
        {
            if (TryCreateUnifiedToolCallPart(block, out var toolPart))
            {
                yield return toolPart;
                continue;
            }

            switch (block.Type)
            {
                case "text":
                    yield return new AITextContentPart
                    {
                        Type = "text",
                        Text = block.Text ?? string.Empty,
                        Metadata = CreateBlockMetadata(block)
                    };
                    break;
                case "thinking":
                case "redacted_thinking":
                    yield return new AIReasoningContentPart
                    {
                        Type = "reasoning",
                        Text = block.Thinking ?? block.Data,
                        Metadata = CreateBlockMetadata(block)
                    };
                    break;
                case "image":
                case "document":
                case "container_upload":
                    yield return ToUnifiedFilePart(block);
                    break;
                default:
                    yield return new AIFileContentPart
                    {
                        Type = "file",
                        MediaType = "application/json",
                        Filename = block.Title ?? block.Name ?? block.Id,
                        Data = JsonSerializer.SerializeToElement(block, Json),
                        Metadata = CreateBlockMetadata(block)
                    };
                    break;
            }
        }
    }

    private static MessagesContent ToMessagesContent(IEnumerable<AIContentPart>? parts)
    {
        var blocks = new List<MessageContentBlock>();
        foreach (var part in parts ?? [])
        {
            if (part is AIToolCallContentPart toolPart)
            {
                foreach (var (assistantBlock, userBlock) in ToMessageToolBlocks(toolPart))
                {
                    if (assistantBlock is not null)
                        blocks.Add(assistantBlock);

                    if (userBlock is not null)
                        blocks.Add(userBlock);
                }
                continue;
            }

            var raw = ExtractRawBlock(part.Metadata);
            if (raw is not null)
            {
                blocks.Add(raw);
                continue;
            }

            switch (part)
            {
                case AITextContentPart text:
                    blocks.Add(new MessageContentBlock { Type = "text", Text = text.Text });
                    break;
                case AIReasoningContentPart reasoning:
                    blocks.Add(new MessageContentBlock { Type = "thinking", Thinking = reasoning.Text });
                    break;
                case AIFileContentPart file:
                    blocks.Add(ToMessageFileBlock(file));
                    break;
            }
        }

        if (blocks.Count == 1 && blocks[0].Type == "text")
            return new MessagesContent(blocks[0].Text ?? string.Empty);

        return new MessagesContent(blocks);
    }

    private static AIToolDefinition ToUnifiedTool(MessageToolDefinition tool)
        => new()
        {
            Name = tool.Name ?? tool.Type ?? "tool",
            Title = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
            Metadata = new Dictionary<string, object?>
            {
                ["messages.tool.raw"] = JsonSerializer.SerializeToElement(tool, Json),
                ["messages.tool.type"] = tool.Type
            }
        };

    private static MessageToolDefinition ToMessageTool(AIToolDefinition tool)
    {
        if (tool.Metadata is not null && tool.Metadata.TryGetValue("messages.tool.raw", out var raw) && raw is not null)
        {
            var hydrated = DeserializeFromObject<MessageToolDefinition>(raw);
            if (hydrated is not null)
                return hydrated;
        }

        return new MessageToolDefinition
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = SerializeToNullableElement(tool.InputSchema),
            Type = ExtractValue<string>(tool.Metadata, "messages.tool.type") ?? "custom"
        };
    }

    private static MessageToolChoice? ToMessageToolChoice(object? toolChoice, bool? parallelToolCalls)
    {
        var choice = DeserializeFromObject<MessageToolChoice>(toolChoice);
        if (choice is not null)
            return choice;

        return toolChoice is null && parallelToolCalls is null
            ? null
            : new MessageToolChoice
            {
                Type = "auto",
                DisableParallelToolUse = parallelToolCalls.HasValue ? !parallelToolCalls.Value : null
            };
    }

    private static IEnumerable<AIEventEnvelope> ToUnifiedEnvelopes(MessageStreamPart part, MessagesStreamMappingState state)
    {
        switch (part.Type)
        {
            case "message_start":
                state.CurrentMessage = part.Message;
                state.Usage = part.Message?.Usage;
                yield break;

            case "content_block_start":
                if (part.Index is null || part.ContentBlock is null)
                    yield break;

                var blockState = state.GetOrCreate(part.Index.Value, part.ContentBlock, part.Message?.Id ?? state.CurrentMessage?.Id);
                var eventId = ResolveStreamEventId(part.ContentBlock, blockState.EventId);

                if (part.ContentBlock.Type == "text")
                {
                    yield return CreateEnvelope("text-start", eventId);
                }
                else if (part.ContentBlock.Type is "thinking" or "redacted_thinking")
                {
                    yield return CreateEnvelope("reasoning-start", eventId, new Dictionary<string, object?>
                    {
                        ["signature"] = part.ContentBlock.Signature,
                        ["encrypted_content"] = part.ContentBlock.Data
                    });
                }
                else if (IsToolInputBlock(part.ContentBlock.Type))
                {
                    yield return CreateEnvelope("tool-input-start", eventId, new Dictionary<string, object?>
                    {
                        ["toolName"] = part.ContentBlock.Name ?? part.ContentBlock.Type,
                        ["title"] = part.ContentBlock.Name,
                        ["providerExecuted"] = IsProviderExecutedTool(part.ContentBlock.Type)
                    });
                }
                else if (IsToolOutputBlock(part.ContentBlock.Type))
                {
                    yield return CreateEnvelope("tool-output-available", eventId, new Dictionary<string, object?>
                    {
                        ["providerExecuted"] = true,
                        ["output"] = SerializeBlockOutput(part.ContentBlock)
                    });
                }

                foreach (var sourceEvent in CreateSourceEnvelopes(part.ContentBlock, eventId))
                    yield return sourceEvent;
                yield break;

            case "content_block_delta":
                if (part.Index is null || part.Delta is null || !state.Blocks.TryGetValue(part.Index.Value, out var deltaState))
                    yield break;

                switch (part.Delta.Type)
                {
                    case "text_delta":
                        yield return CreateEnvelope("text-delta", deltaState.EventId, part.Delta.Text ?? string.Empty);
                        break;
                    case "thinking_delta":
                        yield return CreateEnvelope("reasoning-delta", deltaState.EventId, part.Delta.Thinking ?? string.Empty);
                        break;
                    case "signature_delta":
                        deltaState.Signature = part.Delta.Signature;
                        break;
                    case "input_json_delta":
                        if (!string.IsNullOrEmpty(part.Delta.PartialJson))
                        {
                            deltaState.InputJson.Append(part.Delta.PartialJson);
                            yield return CreateEnvelope("tool-input-delta", deltaState.EventId, part.Delta.PartialJson);
                        }
                        break;
                }
                yield break;

            case "content_block_stop":
                if (part.Index is null || !state.Blocks.TryGetValue(part.Index.Value, out var stopState))
                    yield break;

                if (stopState.BlockType == "text")
                {
                    yield return CreateEnvelope("text-end", stopState.EventId);
                }
                else if (stopState.BlockType is "thinking" or "redacted_thinking")
                {
                    yield return CreateEnvelope("reasoning-end", stopState.EventId, new Dictionary<string, object?>
                    {
                        ["signature"] = stopState.Signature,
                        ["encrypted_content"] = stopState.Block.Data
                    });
                }
                else if (IsToolInputBlock(stopState.BlockType))
                {
                    yield return CreateEnvelope("tool-input-available", stopState.EventId, new Dictionary<string, object?>
                    {
                        ["toolName"] = stopState.Block.Name ?? stopState.BlockType,
                        ["title"] = stopState.Block.Name,
                        ["providerExecuted"] = IsProviderExecutedTool(stopState.BlockType),
                        ["input"] = JsonDocument.Parse(stopState.InputJson.ToString()).RootElement
                    });
                }

                yield break;

            case "message_delta":
                state.Usage = MergeUsage(state.Usage, part.Usage);
                state.StopReason = part.Delta?.StopReason ?? state.StopReason;
                state.StopSequence = part.Delta?.StopSequence ?? state.StopSequence;
                yield break;

            case "message_stop":
                yield return CreateEnvelope("finish", state.CurrentMessage?.Id, new Dictionary<string, object?>
                {
                    ["model"] = state.CurrentMessage?.Model,
                    ["completed_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["inputTokens"] = state.Usage?.InputTokens,
                    ["outputTokens"] = state.Usage?.OutputTokens,
                    ["totalTokens"] = (state.Usage?.InputTokens ?? 0)
                        + (state.Usage?.OutputTokens ?? 0)
                        + (state.Usage?.CacheCreationInputTokens ?? 0)
                        + (state.Usage?.CacheReadInputTokens ?? 0),
                    ["finishReason"] = ToUiFinishReason(state.StopReason),
                    ["stopSequence"] = state.StopSequence
                });
                state.Reset();
                yield break;

            case "error":
                yield return CreateEnvelope("error", state.CurrentMessage?.Id, new Dictionary<string, object?>
                {
                    ["errorText"] = part.Error?.Message ?? part.AdditionalProperties?.GetValueOrDefault("error").ToString() ?? "Messages stream error"
                });
                yield break;

            case "ping":
                yield break;
        }
    }

    public static T? GetProviderOption<T>(
         this MessagesRequestMetadata metadata,
        string providerId,
        string key)
    {
        if (metadata is null)
            return default;

        if (metadata.AdditionalProperties?.TryGetValue(providerId, out var provider) != true)
            return default;

        if (!provider.TryGetProperty(key, out var value))
            return default;

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default;

        return value.Deserialize<T>(JsonSerializerOptions.Web);
    }

    public static List<MessageToolDefinition>? GetMessageToolDefinitions(
        this MessagesRequestMetadata metadata,
        //this Dictionary<string, object?>? metadata,
        string providerId)
    {
        if (metadata is null)
            return null;

        if (metadata.AdditionalProperties?.TryGetValue(providerId, out var providerObj) != true)
            return null;

        if (!providerObj.TryGetProperty("tools", out var toolsEl) ||
            toolsEl.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<MessageToolDefinition>();

        foreach (var toolEl in toolsEl.EnumerateArray())
        {
            try
            {
                var def = toolEl.Deserialize<MessageToolDefinition>(JsonSerializerOptions.Web);
                if (def != null)
                    result.Add(def);
            }
            catch
            {
                // ignore invalid tool entries (passthrough safety)
            }
        }

        return result.Count > 0 ? result : null;
    }


    private static IEnumerable<AIEventEnvelope> CreateSourceEnvelopes(MessageContentBlock block, string? id)
    {
        foreach (var citation in block.Citations ?? [])
        {
            if (citation.Type == "web_search_result_location" && !string.IsNullOrWhiteSpace(citation.Url))
            {
                yield return CreateEnvelope("source-url", id, new Dictionary<string, object?>
                {
                    ["sourceId"] = citation.EncryptedIndex ?? citation.Url,
                    ["url"] = citation.Url,
                    ["title"] = citation.Title ?? citation.Url,
                    ["type"] = citation.Type
                });
            }
        }
    }

    private static AIEventEnvelope CreateEnvelope(string type, string? id, object? data = null)
        => new()
        {
            Type = type,
            Id = id,
            Data = data,
            Timestamp = DateTimeOffset.UtcNow
        };

    private static AIEventEnvelope CreateDataEnvelope(string type, object? data)
        => new()
        {
            Type = type,
            Data = data,
            Timestamp = DateTimeOffset.UtcNow
        };

    private static AIFileContentPart ToUnifiedFilePart(MessageContentBlock block)
    {
        var source = block.Source;
        var data = source?.Url ?? source?.Data ?? source?.Content?.Text ?? (object?)JsonSerializer.SerializeToElement(block, Json);

        return new AIFileContentPart
        {
            Type = "file",
            MediaType = source?.MediaType ?? GuessMediaType(source?.Url),
            Filename = block.Title ?? block.FileId ?? block.Id,
            Data = data,
            Metadata = CreateBlockMetadata(block)
        };
    }

    private static List<AIContentPart>? ToUnifiedToolLikeContent(MessageContentBlock block)
    {
        if (block.Content is null)
            return null;

        return ToUnifiedContentParts(block.Content).ToList();
    }

    private static IEnumerable<AIOutputItem> ToSourceOutputItems(MessageContentBlock block)
    {
        foreach (var citation in block.Citations ?? [])
        {
            if (citation.Type != "web_search_result_location" || string.IsNullOrWhiteSpace(citation.Url))
                continue;

            yield return new AIOutputItem
            {
                Type = "source-url",
                Content =
                [
                    new AITextContentPart
                    {
                        Type = "text",
                        Text = citation.Title ?? citation.Url,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["messages.source.raw"] = JsonSerializer.SerializeToElement(citation, Json)
                        }
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    ["messages.source.url"] = citation.Url,
                    ["messages.source.title"] = citation.Title,
                    ["messages.source.type"] = citation.Type,
                    ["messages.source.raw"] = JsonSerializer.SerializeToElement(citation, Json)
                }
            };
        }
    }

    private static Dictionary<string, object?> CreateBlockMetadata(MessageContentBlock block)
        => new()
        {
            ["messages.block.type"] = block.Type,
            ["messages.block.raw"] = JsonSerializer.SerializeToElement(block, Json)
        };

    private static MessageContentBlock? ExtractRawBlock(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("messages.block.raw", out var raw) || raw is null)
            return null;

        return DeserializeFromObject<MessageContentBlock>(raw);
    }

    private static MessageContentBlock ToMessageFileBlock(AIFileContentPart file)
    {
        var raw = ExtractRawBlock(file.Metadata);
        if (raw is not null)
            return raw;

        if (file.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new MessageContentBlock
            {
                Type = "image",
                Source = new MessageSource
                {
                    Type = file.Data?.ToString()?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? "url" : "base64",
                    Url = file.Data?.ToString()?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? file.Data?.ToString() : null,
                    Data = file.Data?.ToString()?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? null : file.Data?.ToString(),
                    MediaType = file.MediaType
                },
                Title = file.Filename
            };
        }

        return new MessageContentBlock
        {
            Type = "document",
            Source = new MessageSource
            {
                Type = file.Data?.ToString()?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? "url" : "text",
                Url = file.Data?.ToString()?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? file.Data?.ToString() : null,
                Data = file.Data?.ToString()?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? null : file.Data?.ToString(),
                MediaType = file.MediaType ?? "text/plain"
            },
            Title = file.Filename,
            FileId = file.Filename
        };
    }

    private static object? ParseToolInput(StreamBlockState state)
    {
        if (state.Block.Input is JsonElement input && input.ValueKind != JsonValueKind.Undefined)
            return input;

        if (state.InputJson.Length == 0)
            return JsonSerializer.SerializeToElement(new { }, Json);

        try
        {
            return JsonDocument.Parse(state.InputJson.ToString()).RootElement.Clone();
        }
        catch
        {
            return JsonSerializer.SerializeToElement(new { raw = state.InputJson.ToString() }, Json);
        }
    }

    private static string ResolveStreamEventId(MessageContentBlock block, string fallbackId)
    {
        if (IsToolOutputBlock(block.Type) && !string.IsNullOrWhiteSpace(block.ToolUseId))
            return block.ToolUseId!;

        if (IsToolInputBlock(block.Type) && !string.IsNullOrWhiteSpace(block.Id))
            return block.Id!;

        if (!string.IsNullOrWhiteSpace(block.ToolUseId))
            return block.ToolUseId!;

        if (!string.IsNullOrWhiteSpace(block.Id))
            return block.Id!;

        return fallbackId;
    }

    private static object SerializeBlockOutput(MessageContentBlock block)
    {
        if (block.Type == "web_search_tool_result")
        {
            var results = block.Content?.IsBlocks == true
                ? block.Content.Blocks
                : null;

            return new ModelContextProtocol.Protocol.CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    results = results
                }, Json)
            };
        }

        if (block.Content is { IsText: true })
            return block.Content.Text ?? string.Empty;

        if (block.Content is { IsBlocks: true })
            return JsonSerializer.SerializeToElement(block.Content.Blocks, Json);

        if (block.Content is { IsRaw: true })
            return block.Content.Raw!.Value.Clone();

        return JsonSerializer.SerializeToElement(block, Json);
    }

    private static bool TryCreateUnifiedToolCallPart(
        MessageContentBlock block,
        out AIToolCallContentPart toolPart)
    {
        if (IsToolInputBlock(block.Type))
        {
            toolPart = new AIToolCallContentPart
            {
                Type = block.Type,
                ToolCallId = block.Id ?? block.ToolUseId ?? Guid.NewGuid().ToString("N"),
                ToolName = block.Name ?? block.ToolName,
                Title = block.Title ?? block.Name,
                Input = block.Input?.Clone(),
                State = "input-available",
                ProviderExecuted = IsProviderExecutedTool(block.Type),
                Metadata = CreateBlockMetadata(block)
            };

            return true;
        }

        if (IsToolOutputBlock(block.Type))
        {
            toolPart = new AIToolCallContentPart
            {
                Type = block.Type,
                ToolCallId = block.ToolUseId ?? block.Id ?? Guid.NewGuid().ToString("N"),
                ToolName = block.Name ?? block.ToolName,
                Title = block.Title ?? block.Name,
                Output = block.IsError == true
                    ? JsonSerializer.SerializeToElement(new { error = FlattenContentText(block.Content) }, Json)
                    : SerializeBlockOutput(block),
                State = block.IsError == true ? "output-error" : "output-available",
                ProviderExecuted = block.Type != "tool_result",
                Metadata = CreateBlockMetadata(block)
            };

            return true;
        }

        toolPart = null!;
        return false;
    }

    private static IEnumerable<(MessageContentBlock? AssistantBlock, MessageContentBlock? UserBlock)> ToMessageToolBlocks(AIToolCallContentPart toolPart)
    {
        if (toolPart.IsProviderToolCall)
            yield break;

        yield return (
            new MessageContentBlock
            {
                Type = "tool_use",
                Id = toolPart.ToolCallId,
                Name = toolPart.ToolName ?? toolPart.Title ?? "tool",
                Input = SerializeToNullableElement(toolPart.Input) ?? JsonSerializer.SerializeToElement(new { }, Json)
            },
            null);

        if (!HasToolOutput(toolPart))
            yield break;

        yield return (
            null,
            new MessageContentBlock
            {
                Type = "tool_result",
                ToolUseId = toolPart.ToolCallId,
                Content = ToMessageToolOutputContent(toolPart),
                IsError = string.Equals(toolPart.State, "output-error", StringComparison.OrdinalIgnoreCase)
            });
    }

    private static MessagesContent CreateMessagesContentFromBlocks(List<MessageContentBlock> blocks)
        => blocks.Count == 1 && blocks[0].Type == "text"
            ? new MessagesContent(blocks[0].Text ?? string.Empty)
            : new MessagesContent(blocks);

    private static MessagesContent ToMessageToolOutputContent(AIToolCallContentPart toolPart)
    {
        return toolPart.Output switch
        {
            null => new MessagesContent(string.Empty),
            JsonElement json when json.ValueKind == JsonValueKind.String => new MessagesContent(json.GetString() ?? string.Empty),
            JsonElement json => new MessagesContent(json.GetRawText()),
            string text => new MessagesContent(text),
            _ => new MessagesContent(JsonSerializer.SerializeToElement(toolPart.Output, Json).GetRawText())
        };
    }

    private static bool HasToolOutput(AIToolCallContentPart toolPart)
        => toolPart.Output is not null;

    private static MessagesUsage? MergeUsage(MessagesUsage? current, MessagesUsage? update)
    {
        if (update is null)
            return current;

        current ??= new MessagesUsage();
        current.InputTokens ??= update.InputTokens;
        current.OutputTokens = update.OutputTokens ?? current.OutputTokens;
        current.CacheCreationInputTokens = update.CacheCreationInputTokens ?? current.CacheCreationInputTokens;
        current.CacheReadInputTokens = update.CacheReadInputTokens ?? current.CacheReadInputTokens;
        current.ServiceTier ??= update.ServiceTier;
        current.InferenceGeo ??= update.InferenceGeo;
        current.CacheCreation ??= update.CacheCreation;
        current.ServerToolUse ??= update.ServerToolUse;
        return current;
    }

    private static bool IsToolInputBlock(string? type)
        => type is "tool_use" or "server_tool_use" or "mcp_tool_use";

    private static bool IsProviderExecutedTool(string? type)
        => type is "server_tool_use" or "mcp_tool_use";

    private static bool IsToolOutputBlock(string? type)
        => type is "tool_result"
            or "mcp_tool_result"
            or "web_search_tool_result"
            or "web_fetch_tool_result"
            or "code_execution_tool_result"
            or "bash_code_execution_tool_result"
            or "text_editor_code_execution_tool_result"
            or "tool_search_tool_result";

    private static string NormalizeRole(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            "assistant" => "assistant",
            _ => "user"
        };

    private static string? FlattenContentText(MessagesContent? content)
    {
        if (content is null)
            return null;

        if (content.IsText)
            return content.Text;

        var texts = (content.Blocks ?? [])
            .Where(a => a.Type == "text" && !string.IsNullOrWhiteSpace(a.Text))
            .Select(a => a.Text)
            .ToList();

        return texts.Count == 0 ? null : string.Join("\n\n", texts);
    }

    private static string ToUnifiedStatus(string? stopReason)
        => stopReason?.Trim().ToLowerInvariant() switch
        {
            "refusal" => "filtered",
            "pause_turn" => "in_progress",
            _ => "completed"
        };

    private static string? ToMessagesStopReason(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "filtered" => "refusal",
            "in_progress" => "pause_turn",
            _ => "end_turn"
        };

    private static string ToUiFinishReason(string? stopReason)
        => stopReason?.Trim().ToLowerInvariant() switch
        {
            "tool_use" => "tool-calls",
            "max_tokens" => "length",
            "stop_sequence" => "stop",
            "pause_turn" => "other",
            "refusal" => "content-filter",
            _ => "stop"
        };

    private static string? GuessMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var end = value.IndexOf(';');
            if (end > 5)
                return value[5..end];
        }

        return null;
    }

    private static T? ExtractObject<T>(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return default;

        return DeserializeFromObject<T>(value);
    }

    private static T? ExtractValue<T>(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return default;

        return DeserializeFromObject<T>(value);
    }

    private static T? DeserializeFromObject<T>(object? value)
    {
        if (value is null)
            return default;

        if (value is T cast)
            return cast;

        try
        {
            if (value is JsonElement json)
                return JsonSerializer.Deserialize<T>(json.GetRawText(), Json);

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json);
        }
        catch
        {
            return default;
        }
    }

    private static JsonElement? SerializeToNullableElement(object? value)
    {
        if (value is null)
            return null;

        try
        {
            return value is JsonElement json ? json.Clone() : JsonSerializer.SerializeToElement(value, Json);
        }
        catch
        {
            return null;
        }
    }

    public sealed class MessagesStreamMappingState
    {
        internal readonly Dictionary<int, StreamBlockState> Blocks = new();

        internal MessagesResponse? CurrentMessage { get; set; }

        internal MessagesUsage? Usage { get; set; }

        internal string? StopReason { get; set; }

        internal string? StopSequence { get; set; }

        internal StreamBlockState GetOrCreate(int index, MessageContentBlock block, string? messageId)
        {
            if (Blocks.TryGetValue(index, out var existing))
                return existing;

            var eventId = ResolveStreamEventId(block, $"{messageId ?? "msg"}:{index}:{block.Type}");
            var created = new StreamBlockState(index, eventId, block);
            Blocks[index] = created;
            return created;
        }

        internal void Reset()
        {
            Blocks.Clear();
            CurrentMessage = null;
            Usage = null;
            StopReason = null;
            StopSequence = null;
        }
    }

    internal sealed class StreamBlockState
    {
        public StreamBlockState(int index, string eventId, MessageContentBlock block)
        {
            Index = index;
            EventId = eventId;
            Block = block;
            BlockType = block.Type;
        }

        public int Index { get; }

        public string EventId { get; }

        public string BlockType { get; }

        public MessageContentBlock Block { get; }

        public StringBuilder InputJson { get; } = new();

        public string? Signature { get; set; }
    }
}
