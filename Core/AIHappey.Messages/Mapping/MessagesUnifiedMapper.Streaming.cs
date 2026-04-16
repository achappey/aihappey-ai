using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Messages.Mapping;

public static partial class MessagesUnifiedMapper
{
    public static IEnumerable<AIStreamEvent> ToUnifiedStreamEvents(
        this MessageStreamPart part,
        string providerId,
        MessagesStreamMappingState? state = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        state ??= new MessagesStreamMappingState();

        foreach (var envelope in ToUnifiedEnvelopes(part, providerId, state))
        {
            var metadata = new Dictionary<string, object?>
            {
                ["messages.stream.type"] = part.Type,
                ["messages.stream.raw"] = JsonSerializer.SerializeToElement(part, Json)
            };

            if (envelope.Data is AIFinishEventData finishData && finishData.MessageMetadata is not null)
            {
                foreach (var item in finishData.MessageMetadata.ToDictionary())
                    metadata[item.Key] = item.Value;
            }

            yield return new AIStreamEvent
            {
                ProviderId = providerId,
                Event = envelope,
                Metadata = metadata
            };
        }

    }

    public static MessageStreamPart? ToMessageStreamPart(
        this AIStreamEvent streamEvent,
        MessagesReverseStreamMappingState? state = null)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        MessageStreamPart? single = null;
        var count = 0;

        foreach (var part in streamEvent.ToMessageStreamParts(state))
        {
            count++;
            if (count == 1)
                single = part;
        }

        return count switch
        {
            0 => null,
            1 => single,
            _ => throw new InvalidOperationException(
                $"Unified event '{streamEvent.Event.Type}' expands to multiple message stream parts. Use ToMessageStreamParts instead.")
        };
    }

    public static IEnumerable<MessageStreamPart> ToMessageStreamParts(
        this AIStreamEvent streamEvent,
        MessagesReverseStreamMappingState? state = null)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        state ??= new MessagesReverseStreamMappingState();

        if (TryGetRawMessageStreamPart(streamEvent, state, out var rawPart))
        {
            if (rawPart is not null)
                yield return rawPart;

            yield break;
        }

        foreach (var part in ToSyntheticMessageStreamParts(streamEvent, state))
            yield return part;
    }

    private static IEnumerable<AIEventEnvelope> ToUnifiedEnvelopes(MessageStreamPart part, string providerId, MessagesStreamMappingState state)
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
                    var activeTextEventId = state.EnsureActiveTextEventId(eventId);

                    foreach (var sourceEvent in CreateSourceEnvelopes(part.ContentBlock, activeTextEventId, state))
                        yield return sourceEvent;

                    yield break;
                }

                if (state.CloseActiveTextSpan() is { } activeTextToEnd)
                {
                    yield return CreateEnvelope("text-end", activeTextToEnd, new AITextEndEventData());
                }

                if (part.ContentBlock.Type is "thinking" or "redacted_thinking")
                {
                    yield return CreateEnvelope("reasoning-start", eventId, new AIReasoningStartEventData
                    {
                        ProviderMetadata = CreateReasoningProviderMetadata(
                            providerId,
                            signature: part.ContentBlock.Signature,
                            encryptedContent: part.ContentBlock.Data)
                    });
                }
                else if (IsToolInputBlock(part.ContentBlock.Type))
                {
                    yield return CreateEnvelope("tool-input-start", eventId, new AIToolInputStartEventData
                    {
                        ToolName = part.ContentBlock.Name ?? part.ContentBlock.Type,
                        Title = part.ContentBlock.Name,
                        ProviderExecuted = IsProviderExecutedTool(part.ContentBlock.Type),
                        ProviderMetadata = CreateProviderExecutedToolProviderMetadata(
                            providerId,
                            IsProviderExecutedTool(part.ContentBlock.Type))
                    });
                }
                else if (IsToolOutputBlock(part.ContentBlock.Type))
                {
                    yield return CreateEnvelope("tool-output-available", eventId, new AIToolOutputAvailableEventData
                    {
                        ProviderExecuted = true,
                        Output = SerializeBlockOutput(part.ContentBlock) ?? new { },
                        ProviderMetadata = CreateToolOutputProviderMetadata(providerId, part.ContentBlock)
                    });
                }

                foreach (var sourceEvent in CreateSourceEnvelopes(part.ContentBlock, eventId, state))
                    yield return sourceEvent;
                yield break;

            case "content_block_delta":
                if (part.Index is null || part.Delta is null || !state.Blocks.TryGetValue(part.Index.Value, out var deltaState))
                    yield break;

                switch (part.Delta.Type)
                {
                    case "text_delta":
                        var textEventId = state.EnsureActiveTextEventId(deltaState.EventId);

                        if (state.MarkActiveTextStarted())
                        {
                            yield return CreateEnvelope("text-start", textEventId, new AITextStartEventData());
                        }

                        yield return CreateEnvelope("text-delta", textEventId, new AITextDeltaEventData
                        {
                            Delta = part.Delta.Text ?? string.Empty
                        });
                        break;
                    case "thinking_delta":
                        yield return CreateEnvelope("reasoning-delta", deltaState.EventId, new AIReasoningDeltaEventData
                        {
                            Delta = part.Delta.Thinking ?? string.Empty
                        });
                        break;
                    case "signature_delta":
                        deltaState.Signature = part.Delta.Signature;
                        break;
                    case "input_json_delta":
                        if (!string.IsNullOrEmpty(part.Delta.PartialJson))
                        {
                            deltaState.InputJson.Append(part.Delta.PartialJson);
                            yield return CreateEnvelope("tool-input-delta", deltaState.EventId, new AIToolInputDeltaEventData
                            {
                                InputTextDelta = part.Delta.PartialJson
                            });
                        }
                        break;
                    case "citations_delta":
                        if (TryCreateSourceEnvelope(part.Delta.Citation, deltaState.EventId, state, out var sourceEnvelope))
                            yield return sourceEnvelope;
                        break;
                }
                yield break;

            case "content_block_stop":
                if (part.Index is null || !state.Blocks.TryGetValue(part.Index.Value, out var stopState))
                    yield break;

                if (stopState.BlockType == "text")
                {
                    yield break;
                }

                if (stopState.BlockType is "thinking" or "redacted_thinking")
                {
                    yield return CreateEnvelope("reasoning-end", stopState.EventId, new AIReasoningEndEventData
                    {
                        ProviderMetadata = CreateReasoningProviderMetadata(
                            providerId,
                            signature: stopState.Signature,
                            encryptedContent: stopState.Block.Data)
                    });
                }
                else if (IsToolInputBlock(stopState.BlockType))
                {
                    var providerExecuted = IsProviderExecutedTool(stopState.BlockType);

                    yield return CreateEnvelope("tool-input-available", stopState.EventId, new AIToolInputAvailableEventData
                    {
                        ToolName = stopState.Block.Name ?? stopState.BlockType,
                        Title = stopState.Block.Name,
                        ProviderExecuted = providerExecuted,
                        Input = ParseToolInput(stopState) ?? JsonSerializer.SerializeToElement(new { }, Json),
                        ProviderMetadata = CreateProviderExecutedToolProviderMetadata(providerId, providerExecuted)
                    });
                }

                yield break;

            case "message_delta":
                state.Usage = MergeUsage(state.Usage, part.Usage);
                state.StopReason = part.Delta?.StopReason ?? state.StopReason;
                state.StopSequence = part.Delta?.StopSequence ?? state.StopSequence;
                yield break;

            case "message_stop":
                if (state.CloseActiveTextSpan() is { } finalTextEventId)
                {
                    yield return CreateEnvelope("text-end", finalTextEventId, new AITextEndEventData());
                }

                Dictionary<string, object>? finishMetadata = null;
                if (part.Metadata is not null || state.Usage is not null)
                {
                    finishMetadata = part.Metadata is null
                        ? []
                        : part.Metadata.ToDictionary(a => a.Key, a => (object)a.Value);

                    finishMetadata["usage"] = state.Usage;
                }

                yield return CreateEnvelope("finish", state.CurrentMessage?.Id, new AIFinishEventData
                {
                    Model = state.CurrentMessage?.Model,
                    CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    InputTokens = state.Usage?.InputTokens,
                    OutputTokens = state.Usage?.OutputTokens,
                    TotalTokens = (state.Usage?.InputTokens ?? 0)
                        + (state.Usage?.OutputTokens ?? 0)
                        + (state.Usage?.CacheCreationInputTokens ?? 0)
                        + (state.Usage?.CacheReadInputTokens ?? 0),
                    MessageMetadata = finishMetadata is null
                        ? null
                        : AIFinishMessageMetadata.FromDictionary(
                            finishMetadata,
                            fallbackModel: state.CurrentMessage?.Model,
                            fallbackTimestamp: DateTimeOffset.UtcNow),
                    FinishReason = ToUiFinishReason(state.StopReason),
                    StopSequence = state.StopSequence
                });
                state.Reset();
                yield break;

            case "error":
                yield return CreateEnvelope("error", state.CurrentMessage?.Id, new AIErrorEventData
                {
                    ErrorText = part.Error?.Message ?? part.AdditionalProperties?.GetValueOrDefault("error").ToString() ?? "Messages stream error"
                });
                yield break;

            case "ping":
                yield break;
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
            Type = $"data-messages.{type}",
            Data = new AIDataEventData
            {
                Data = data ?? new { },
                Transient = type.Contains("delta")
            },
            Timestamp = DateTimeOffset.UtcNow
        };

    private static bool TryGetRawMessageStreamPart(
        AIStreamEvent streamEvent,
        MessagesReverseStreamMappingState state,
        out MessageStreamPart? rawPart)
    {
        rawPart = null;

        if (!TryDeserializeRawMessageStreamPart(streamEvent, out var part, out var rawSignature))
            return false;

        if (!state.TryMarkRawPart(rawSignature))
            return true;

        rawPart = part;

        if (part is not null)
            UpdateReverseStateFromRawPart(part, state);

        return true;
    }

    private static bool TryDeserializeRawMessageStreamPart(
        AIStreamEvent streamEvent,
        out MessageStreamPart? part,
        out string rawSignature)
    {
        part = null;
        rawSignature = string.Empty;

        if (TryDeserializeRawMessageStreamPartFromData(streamEvent, out part, out rawSignature))
            return true;

        return TryDeserializeRawMessageStreamPartFromMetadata(streamEvent, out part, out rawSignature);
    }

    private static bool TryDeserializeRawMessageStreamPartFromData(
        AIStreamEvent streamEvent,
        out MessageStreamPart? part,
        out string rawSignature)
    {
        part = null;
        rawSignature = string.Empty;

        if (!streamEvent.Event.Type.StartsWith("data-messages.", StringComparison.OrdinalIgnoreCase))
            return false;

        var dataEvent = DeserializeFromObject<AIDataEventData>(streamEvent.Event.Data);
        if (dataEvent?.Data is null)
            return false;

        part = DeserializeFromObject<MessageStreamPart>(dataEvent.Data);
        if (part is null)
            return false;

        rawSignature = TrySerializeRawSignature(dataEvent.Data)
            ?? JsonSerializer.Serialize(part, Json);

        return true;
    }

    private static bool TryDeserializeRawMessageStreamPartFromMetadata(
        AIStreamEvent streamEvent,
        out MessageStreamPart? part,
        out string rawSignature)
    {
        part = null;
        rawSignature = string.Empty;

        if (streamEvent.Metadata is null
            || !streamEvent.Metadata.TryGetValue("messages.stream.raw", out var raw)
            || raw is null)
        {
            return false;
        }

        part = DeserializeFromObject<MessageStreamPart>(raw);
        if (part is null)
            return false;

        rawSignature = TrySerializeRawSignature(raw)
            ?? JsonSerializer.Serialize(part, Json);

        return true;
    }

    private static string? TrySerializeRawSignature(object? raw)
    {
        try
        {
            return raw switch
            {
                JsonElement json => json.GetRawText(),
                _ => raw is null ? null : JsonSerializer.Serialize(raw, Json)
            };
        }
        catch
        {
            return null;
        }
    }

    private static void UpdateReverseStateFromRawPart(MessageStreamPart part, MessagesReverseStreamMappingState state)
    {
        if (string.Equals(part.Type, "message_start", StringComparison.OrdinalIgnoreCase))
        {
            state.SetMessageContext(part.Message?.Id, part.Message?.Model, part.Message?.Role);
            state.MarkMessageStarted();
            return;
        }

        if (string.Equals(part.Type, "message_stop", StringComparison.OrdinalIgnoreCase))
            state.Reset();
    }

    private static IEnumerable<MessageStreamPart> ToSyntheticMessageStreamParts(
        AIStreamEvent streamEvent,
        MessagesReverseStreamMappingState state)
    {
        var envelope = streamEvent.Event;

        switch (envelope.Type)
        {
            case "text-start":
                {
                    state.SetMessageContext(ResolveMessageId(streamEvent), ResolveModel(streamEvent), ResolveRoleForReverseStream(streamEvent));

                    foreach (var part in EnsureMessageStart(streamEvent, state))
                        yield return part;

                    var block = state.GetOrCreateBlock(envelope.Id, "text");
                    foreach (var part in EnsureContentBlockStart(block, streamEvent, state))
                        yield return part;
                    yield break;
                }
            case "text-delta":
                {
                    state.SetMessageContext(ResolveMessageId(streamEvent), ResolveModel(streamEvent), ResolveRoleForReverseStream(streamEvent));

                    foreach (var part in EnsureMessageStart(streamEvent, state))
                        yield return part;

                    var block = state.GetOrCreateBlock(envelope.Id, "text");
                    foreach (var part in EnsureContentBlockStart(block, streamEvent, state))
                        yield return part;

                    var data = DeserializeFromObject<AITextDeltaEventData>(envelope.Data);
                    yield return new MessageStreamPart
                    {
                        Type = "content_block_delta",
                        Index = block.Index,
                        Delta = new MessageStreamDelta
                        {
                            Type = "text_delta",
                            Text = data?.Delta ?? string.Empty
                        }
                    };
                    yield break;
                }
            case "text-end":
                {
                    if (!state.TryGetBlock(envelope.Id, out var block) || block.StopEmitted)
                        yield break;

                    block.StopEmitted = true;
                    yield return new MessageStreamPart
                    {
                        Type = "content_block_stop",
                        Index = block.Index
                    };
                    yield break;
                }
            case "reasoning-start":
                {
                    state.SetMessageContext(ResolveMessageId(streamEvent), ResolveModel(streamEvent), ResolveRoleForReverseStream(streamEvent));

                    foreach (var part in EnsureMessageStart(streamEvent, state))
                        yield return part;

                    var data = DeserializeFromObject<AIReasoningStartEventData>(envelope.Data);
                    var block = state.GetOrCreateBlock(envelope.Id, ResolveReasoningBlockType(data?.ProviderMetadata, streamEvent.ProviderId));
                    block.Signature = ResolveProviderMetadataString(data?.ProviderMetadata, streamEvent.ProviderId, "signature");
                    block.EncryptedContent = ResolveProviderMetadataString(data?.ProviderMetadata, streamEvent.ProviderId, "encrypted_content");

                    foreach (var part in EnsureContentBlockStart(block, streamEvent, state))
                        yield return part;
                    yield break;
                }
            case "reasoning-delta":
                {
                    state.SetMessageContext(ResolveMessageId(streamEvent), ResolveModel(streamEvent), ResolveRoleForReverseStream(streamEvent));

                    foreach (var part in EnsureMessageStart(streamEvent, state))
                        yield return part;

                    var block = state.GetOrCreateBlock(envelope.Id, "thinking");
                    foreach (var part in EnsureContentBlockStart(block, streamEvent, state))
                        yield return part;

                    var data = DeserializeFromObject<AIReasoningDeltaEventData>(envelope.Data);
                    yield return new MessageStreamPart
                    {
                        Type = "content_block_delta",
                        Index = block.Index,
                        Delta = new MessageStreamDelta
                        {
                            Type = "thinking_delta",
                            Thinking = data?.Delta ?? string.Empty
                        }
                    };
                    yield break;
                }
            case "reasoning-end":
                {
                    if (!state.TryGetBlock(envelope.Id, out var block) || block.StopEmitted)
                        yield break;

                    var data = DeserializeFromObject<AIReasoningEndEventData>(envelope.Data);
                    block.Signature ??= ResolveProviderMetadataString(data?.ProviderMetadata, streamEvent.ProviderId, "signature");
                    block.EncryptedContent ??= ResolveProviderMetadataString(data?.ProviderMetadata, streamEvent.ProviderId, "encrypted_content");
                    block.StopEmitted = true;

                    yield return new MessageStreamPart
                    {
                        Type = "content_block_stop",
                        Index = block.Index
                    };
                    yield break;
                }
            case "tool-input-start":
                {
                    state.SetMessageContext(ResolveMessageId(streamEvent), ResolveModel(streamEvent), ResolveRoleForReverseStream(streamEvent));

                    foreach (var part in EnsureMessageStart(streamEvent, state))
                        yield return part;

                    var data = DeserializeFromObject<AIToolInputStartEventData>(envelope.Data);
                    var block = state.GetOrCreateBlock(envelope.Id, ResolveToolInputBlockType(data?.ProviderExecuted));
                    block.ToolName = data?.ToolName ?? block.ToolName;
                    block.Title = data?.Title ?? block.Title;
                    block.ProviderExecuted = data?.ProviderExecuted ?? block.ProviderExecuted;

                    foreach (var part in EnsureContentBlockStart(block, streamEvent, state))
                        yield return part;
                    yield break;
                }
            case "tool-input-delta":
                {
                    state.SetMessageContext(ResolveMessageId(streamEvent), ResolveModel(streamEvent), ResolveRoleForReverseStream(streamEvent));

                    foreach (var part in EnsureMessageStart(streamEvent, state))
                        yield return part;

                    var block = state.GetOrCreateBlock(envelope.Id, "tool_use");
                    foreach (var part in EnsureContentBlockStart(block, streamEvent, state))
                        yield return part;

                    var data = DeserializeFromObject<AIToolInputDeltaEventData>(envelope.Data);
                    block.InputDeltaSeen = true;
                    yield return new MessageStreamPart
                    {
                        Type = "content_block_delta",
                        Index = block.Index,
                        Delta = new MessageStreamDelta
                        {
                            Type = "input_json_delta",
                            PartialJson = data?.InputTextDelta ?? string.Empty
                        }
                    };
                    yield break;
                }
            case "tool-input-available":
                {
                    state.SetMessageContext(ResolveMessageId(streamEvent), ResolveModel(streamEvent), ResolveRoleForReverseStream(streamEvent));

                    foreach (var part in EnsureMessageStart(streamEvent, state))
                        yield return part;

                    var data = DeserializeFromObject<AIToolInputAvailableEventData>(envelope.Data);
                    var block = state.GetOrCreateBlock(envelope.Id, ResolveToolInputBlockType(data?.ProviderExecuted));
                    block.ToolName = data?.ToolName ?? block.ToolName;
                    block.Title = data?.Title ?? block.Title;
                    block.ProviderExecuted = data?.ProviderExecuted ?? block.ProviderExecuted;
                    block.Input = data?.Input;

                    foreach (var part in EnsureContentBlockStart(block, streamEvent, state))
                        yield return part;

                    if (!block.InputDeltaSeen && TrySerializeObjectAsJson(data?.Input, out var inputJson))
                    {
                        block.InputDeltaSeen = true;
                        yield return new MessageStreamPart
                        {
                            Type = "content_block_delta",
                            Index = block.Index,
                            Delta = new MessageStreamDelta
                            {
                                Type = "input_json_delta",
                                PartialJson = inputJson
                            }
                        };
                    }

                    if (!block.StopEmitted)
                    {
                        block.StopEmitted = true;
                        yield return new MessageStreamPart
                        {
                            Type = "content_block_stop",
                            Index = block.Index
                        };
                    }
                    yield break;
                }
            case "tool-output-available":
                {
                    state.SetMessageContext(ResolveMessageId(streamEvent), ResolveModel(streamEvent), ResolveRoleForReverseStream(streamEvent));

                    foreach (var part in EnsureMessageStart(streamEvent, state))
                        yield return part;

                    var data = DeserializeFromObject<AIToolOutputAvailableEventData>(envelope.Data);
                    var block = state.GetOrCreateBlock(
                        ResolveProviderMetadataString(data?.ProviderMetadata, streamEvent.ProviderId, "tool_use_id") ?? envelope.Id,
                        ResolveToolOutputBlockType(data?.ProviderMetadata, streamEvent.ProviderId));

                    if (!block.StartEmitted)
                    {
                        block.StartEmitted = true;
                        block.StopEmitted = true;
                        yield return new MessageStreamPart
                        {
                            Type = "content_block_start",
                            Index = block.Index,
                            ContentBlock = CreateToolOutputBlock(streamEvent, data, block)
                        };

                        yield return new MessageStreamPart
                        {
                            Type = "content_block_stop",
                            Index = block.Index
                        };
                    }
                    yield break;
                }
            case "tool-output-error":
                {
                    state.SetMessageContext(ResolveMessageId(streamEvent), ResolveModel(streamEvent), ResolveRoleForReverseStream(streamEvent));

                    foreach (var part in EnsureMessageStart(streamEvent, state))
                        yield return part;

                    var data = DeserializeFromObject<AIToolOutputErrorEventData>(envelope.Data);
                    var block = state.GetOrCreateBlock(data?.ToolCallId ?? envelope.Id, "tool_result");
                    if (!block.StartEmitted)
                    {
                        block.StartEmitted = true;
                        block.StopEmitted = true;
                        yield return new MessageStreamPart
                        {
                            Type = "content_block_start",
                            Index = block.Index,
                            ContentBlock = new MessageContentBlock
                            {
                                Type = "tool_result",
                                ToolUseId = data?.ToolCallId ?? envelope.Id,
                                Content = new MessagesContent(data?.ErrorText ?? "Tool output error"),
                                IsError = true
                            }
                        };

                        yield return new MessageStreamPart
                        {
                            Type = "content_block_stop",
                            Index = block.Index
                        };
                    }
                    yield break;
                }
            case "source-url":
                {
                    var data = DeserializeFromObject<AISourceUrlEventData>(envelope.Data);
                    if (data is null || !TryResolveCitationBlockIndex(state, envelope.Id, out var index))
                        yield break;

                    yield return new MessageStreamPart
                    {
                        Type = "content_block_delta",
                        Index = index,
                        Delta = new MessageStreamDelta
                        {
                            Type = "citations_delta",
                            Citation = new MessageCitation
                            {
                                Type = data.Type ?? "web_search_result_location",
                                Url = data.Url,
                                Title = data.Title,
                                EncryptedIndex = data.SourceId,
                                FileId = data.FileId,
                                Source = data.Type
                            }
                        }
                    };
                    yield break;
                }
            case "finish":
                {
                    state.SetMessageContext(ResolveMessageId(streamEvent), ResolveModel(streamEvent), ResolveRoleForReverseStream(streamEvent));

                    foreach (var part in EmitOpenBlockStops(state))
                        yield return part;

                    var data = DeserializeFromObject<AIFinishEventData>(envelope.Data);
                    yield return new MessageStreamPart
                    {
                        Type = "message_delta",
                        Usage = CreateMessagesUsage(data),
                        Delta = new MessageStreamDelta
                        {
                            Type = "message_delta",
                            StopReason = ToMessagesStreamStopReason(data?.FinishReason),
                            StopSequence = data?.StopSequence
                        }
                    };

                    yield return new MessageStreamPart
                    {
                        Type = "message_stop",
                        Metadata = data?.MessageMetadata is null
                            ? null
                            : ToJsonElementDictionary(data.MessageMetadata.ToDictionary()
                                .Where(kvp => kvp.Value is not null)
                                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!))
                    };

                    state.Reset();
                    yield break;
                }
            case "error":
                {
                    var data = DeserializeFromObject<AIErrorEventData>(envelope.Data);
                    yield return new MessageStreamPart
                    {
                        Type = "error",
                        Error = new MessageStreamError
                        {
                            Type = "error",
                            Message = data?.ErrorText ?? "Messages stream error"
                        }
                    };
                    yield break;
                }
        }
    }

    private static IEnumerable<MessageStreamPart> EnsureMessageStart(
        AIStreamEvent streamEvent,
        MessagesReverseStreamMappingState state)
    {
        if (state.MessageStarted)
            yield break;

        state.SetMessageContext(ResolveMessageId(streamEvent), ResolveModel(streamEvent), ResolveRoleForReverseStream(streamEvent));
        state.MarkMessageStarted();

        yield return new MessageStreamPart
        {
            Type = "message_start",
            Message = new MessagesResponse
            {
                Id = state.MessageId,
                Model = state.Model,
                Role = state.Role,
                Type = "message",
                Content = []
            }
        };
    }

    private static IEnumerable<MessageStreamPart> EnsureContentBlockStart(
        ReverseStreamBlockState block,
        AIStreamEvent streamEvent,
        MessagesReverseStreamMappingState state)
    {
        if (block.StartEmitted)
            yield break;

        block.StartEmitted = true;
        if (block.BlockType is "text" or "thinking" or "redacted_thinking")
            state.LastCitationBlockIndex = block.Index;

        yield return new MessageStreamPart
        {
            Type = "content_block_start",
            Index = block.Index,
            ContentBlock = CreateSyntheticContentBlock(streamEvent, block)
        };
    }

    private static MessageContentBlock CreateSyntheticContentBlock(
        AIStreamEvent streamEvent,
        ReverseStreamBlockState block)
    {
        if (block.BlockType == "text")
            return new MessageContentBlock { Type = "text", Text = string.Empty };

        if (block.BlockType is "thinking" or "redacted_thinking")
        {
            return new MessageContentBlock
            {
                Type = block.BlockType,
                Signature = block.Signature,
                Data = block.EncryptedContent
            };
        }

        if (IsToolInputBlock(block.BlockType))
        {
            return new MessageContentBlock
            {
                Type = block.BlockType,
                Id = block.Key,
                Name = block.ToolName ?? "tool",
                Title = block.Title,
                ToolName = block.ToolName,
                Input = SerializeToNullableElement(block.Input) ?? JsonSerializer.SerializeToElement(new { }, Json)
            };
        }

        return CreateToolOutputBlock(streamEvent, null, block);
    }

    private static MessageContentBlock CreateToolOutputBlock(
        AIStreamEvent streamEvent,
        AIToolOutputAvailableEventData? data,
        ReverseStreamBlockState block)
    {
        var providerMetadata = data?.ProviderMetadata;
        var blockType = ResolveToolOutputBlockType(providerMetadata, streamEvent.ProviderId);
        var toolCallId = ResolveProviderMetadataString(providerMetadata, streamEvent.ProviderId, "tool_use_id") ?? block.Key;
        var toolName = ResolveProviderMetadataString(providerMetadata, streamEvent.ProviderId, "name")
            ?? ResolveProviderMetadataString(providerMetadata, streamEvent.ProviderId, "tool_name")
            ?? block.ToolName;
        var title = ResolveProviderMetadataString(providerMetadata, streamEvent.ProviderId, "title") ?? block.Title;
        var caller = DeserializeFromObject<MessageCaller>(ResolveProviderMetadataObject(providerMetadata, streamEvent.ProviderId, "caller"));

        var tempMetadata = new Dictionary<string, object?>
        {
            ["messages.provider.id"] = streamEvent.ProviderId,
            ["messages.provider.metadata"] = providerMetadata,
            ["messages.block.type"] = blockType
        };

        var toolPart = new AIToolCallContentPart
        {
            Type = blockType,
            ToolCallId = toolCallId,
            ToolName = toolName,
            Title = title,
            Output = data?.Output,
            ProviderExecuted = data?.ProviderExecuted,
            State = "output-available",
            Metadata = tempMetadata
        };

        if (data?.ProviderExecuted == true
            && TryCreateProviderExecutedToolResultBlock(toolPart, out var providerBlock)
            && providerBlock is not null)
        {
            providerBlock.Type = blockType;
            providerBlock.ToolUseId ??= toolCallId;
            providerBlock.Name ??= toolName;
            providerBlock.ToolName ??= ResolveProviderMetadataString(providerMetadata, streamEvent.ProviderId, "tool_name");
            providerBlock.Title ??= title;
            providerBlock.Caller ??= caller;
            return providerBlock;
        }

        return new MessageContentBlock
        {
            Type = blockType,
            ToolUseId = toolCallId,
            Name = toolName,
            ToolName = ResolveProviderMetadataString(providerMetadata, streamEvent.ProviderId, "tool_name"),
            Title = title,
            Caller = caller,
            Content = ToMessageToolOutputContent(toolPart)
        };
    }

    private static bool TryResolveCitationBlockIndex(
        MessagesReverseStreamMappingState state,
        string? eventId,
        out int index)
    {
        if (state.TryGetBlock(eventId, out var block))
        {
            index = block.Index;
            return true;
        }

        if (state.LastCitationBlockIndex is int lastIndex)
        {
            index = lastIndex;
            return true;
        }

        index = default;
        return false;
    }

    private static IEnumerable<MessageStreamPart> EmitOpenBlockStops(MessagesReverseStreamMappingState state)
    {
        var blocks = state.Blocks.Values
            .Where(a => a.StartEmitted && !a.StopEmitted)
            .OrderBy(a => a.Index)
            .ToList();

        foreach (var block in blocks)
        {
            block.StopEmitted = true;
            yield return new MessageStreamPart
            {
                Type = "content_block_stop",
                Index = block.Index
            };
        }
    }

    private static string ResolveMessageId(AIStreamEvent streamEvent)
        => ExtractValue<string>(streamEvent.Metadata, "messages.response.id")
            ?? ExtractValue<string>(streamEvent.Metadata, "chatcompletions.stream.id")
            ?? ExtractValue<string>(streamEvent.Metadata, "responses.response.id")
            ?? $"msg_{Guid.NewGuid():N}";

    private static string? ResolveModel(AIStreamEvent streamEvent)
        => ExtractValue<string>(streamEvent.Metadata, "messages.response.model")
            ?? ExtractValue<string>(streamEvent.Metadata, "chatcompletions.stream.model")
            ?? ExtractValue<string>(streamEvent.Metadata, "responses.response.model")
            ?? DeserializeFromObject<AIFinishEventData>(streamEvent.Event.Data)?.Model;

    private static string ResolveRoleForReverseStream(AIStreamEvent streamEvent)
        => ExtractValue<string>(streamEvent.Metadata, "messages.response.role")
            ?? "assistant";

    private static string ResolveReasoningBlockType(
        Dictionary<string, Dictionary<string, object>>? providerMetadata,
        string providerId)
        => ResolveProviderMetadataObject(providerMetadata, providerId, "encrypted_content") is null
            ? "thinking"
            : "redacted_thinking";

    private static string ResolveToolInputBlockType(bool? providerExecuted)
        => providerExecuted == true ? "server_tool_use" : "tool_use";

    private static string ResolveToolOutputBlockType(
        Dictionary<string, Dictionary<string, object>>? providerMetadata,
        string providerId)
        => ResolveProviderMetadataString(providerMetadata, providerId, "type") is { } type && IsToolOutputBlock(type)
            ? type
            : "tool_result";

    private static string? ResolveProviderMetadataString(
        Dictionary<string, Dictionary<string, object>>? providerMetadata,
        string providerId,
        string key)
    {
        var value = ResolveProviderMetadataObject(providerMetadata, providerId, key);
        return value switch
        {
            null => null,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            JsonElement json => json.ToString(),
            _ => value.ToString()
        };
    }

    private static object? ResolveProviderMetadataObject(
        Dictionary<string, Dictionary<string, object>>? providerMetadata,
        string providerId,
        string key)
    {
        if (providerMetadata is null
            || string.IsNullOrWhiteSpace(providerId)
            || !providerMetadata.TryGetValue(providerId, out var nested)
            || !nested.TryGetValue(key, out var value))
        {
            return null;
        }

        return value;
    }

    private static bool TrySerializeObjectAsJson(object? value, out string json)
    {
        json = string.Empty;

        if (value is null)
            return false;

        try
        {
            json = value switch
            {
                JsonElement element => element.GetRawText(),
                _ => JsonSerializer.Serialize(value, Json)
            };

            return !string.IsNullOrWhiteSpace(json);
        }
        catch
        {
            return false;
        }
    }

    private static MessagesUsage? CreateMessagesUsage(AIFinishEventData? finishData)
    {
        if (finishData?.InputTokens is null && finishData?.OutputTokens is null && finishData?.MessageMetadata is null)
            return null;

        return new MessagesUsage
        {
            InputTokens = finishData?.InputTokens
                ?? finishData?.MessageMetadata?.InputTokens
                ?? GetUsageInt(finishData?.MessageMetadata?.Usage, "input_tokens", "prompt_tokens", "inputTokens", "promptTokens"),
            OutputTokens = finishData?.OutputTokens
                ?? finishData?.MessageMetadata?.OutputTokens
                ?? GetUsageInt(finishData?.MessageMetadata?.Usage, "output_tokens", "completion_tokens", "outputTokens", "completionTokens")
        };
    }

    private static int? GetUsageInt(JsonElement? usage, params string[] keys)
    {
        if (!usage.HasValue)
            return null;

        var usageValue = usage.Value;

        if (usageValue.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in keys)
        {
            foreach (var property in usageValue.EnumerateObject())
            {
                if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var numericValue))
                    return numericValue;

                if (property.Value.ValueKind == JsonValueKind.String && int.TryParse(property.Value.GetString(), out var parsedValue))
                    return parsedValue;
            }
        }

        return null;
    }

    private static string? ToMessagesStreamStopReason(string? finishReason)
        => finishReason?.Trim().ToLowerInvariant() switch
        {
            "tool-calls" => "tool_use",
            "length" => "max_tokens",
            "stop" => "stop_sequence",
            "other" => "pause_turn",
            "content-filter" => "refusal",
            null => null,
            _ => "end_turn"
        };

    private static Dictionary<string, JsonElement>? ToJsonElementDictionary(Dictionary<string, object>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in metadata)
        {
            result[item.Key] = item.Value switch
            {
                JsonElement json => json.Clone(),
                null => JsonSerializer.SerializeToElement<string?>(null, Json),
                _ => JsonSerializer.SerializeToElement(item.Value, Json)
            };
        }

        return result;
    }

    private static Dictionary<string, Dictionary<string, object>>? CreateReasoningProviderMetadata(
        string providerId,
        string? signature = null,
        object? encryptedContent = null,
        object? summary = null)
    {
        var providerMetadata = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(signature))
            providerMetadata["signature"] = signature;

        if (HasMeaningfulReasoningValue(encryptedContent))
            providerMetadata["encrypted_content"] = encryptedContent!;

        if (HasMeaningfulReasoningValue(summary))
            providerMetadata["summary"] = summary!;

        return providerMetadata.Count == 0
            ? null
            : new Dictionary<string, Dictionary<string, object>>
            {
                [providerId] = providerMetadata
            };
    }

    private static Dictionary<string, Dictionary<string, object>>? CreateProviderExecutedToolProviderMetadata(
        string providerId,
        bool providerExecuted,
        Dictionary<string, object>? providerMetadata = null)
    {
        if (!providerExecuted || string.IsNullOrWhiteSpace(providerId))
            return null;

        return new Dictionary<string, Dictionary<string, object>>
        {
            [providerId] = providerMetadata ?? new Dictionary<string, object>()
        };
    }

    private static Dictionary<string, Dictionary<string, object>>? CreateToolOutputProviderMetadata(
        string providerId,
        MessageContentBlock block)
    {
        var providerMetadata = new Dictionary<string, object>();

        providerMetadata["type"] = block.Type;

        if (!string.IsNullOrWhiteSpace(block.ToolName))
            providerMetadata["tool_name"] = block.ToolName;

        if (!string.IsNullOrWhiteSpace(block.Name))
            providerMetadata["name"] = block.Name;

        if (!string.IsNullOrWhiteSpace(block.Title))
            providerMetadata["title"] = block.Title;

        if (!string.IsNullOrWhiteSpace(block.ToolUseId))
            providerMetadata["tool_use_id"] = block.ToolUseId;

        if (HasMeaningfulReasoningValue(block.Caller))
            providerMetadata["caller"] = JsonSerializer.SerializeToElement(block.Caller, Json);

        return new Dictionary<string, Dictionary<string, object>>
        {
            [providerId] = providerMetadata
        };
    }

    private static bool HasMeaningfulReasoningValue(object? value)
        => value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            JsonElement json => json.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined,
            _ => true
        };

    private static object? ParseToolInput(StreamBlockState state)
    {
        if (state.InputJson.Length > 0)
        {
            try
            {
                return JsonDocument.Parse(state.InputJson.ToString()).RootElement.Clone();
            }
            catch
            {
                return JsonSerializer.SerializeToElement(new { raw = state.InputJson.ToString() }, Json);
            }
        }

        if (state.Block.Input is JsonElement input && input.ValueKind != JsonValueKind.Undefined)
            return input;

        return JsonSerializer.SerializeToElement(new { }, Json);
    }

    private static IEnumerable<AIEventEnvelope> CreateSourceEnvelopes(
        MessageContentBlock block,
        string? id,
        MessagesStreamMappingState state)
    {
        foreach (var citation in block.Citations ?? [])
        {
            if (TryCreateSourceEnvelope(citation, id, state, out var envelope))
                yield return envelope;
        }
    }

    private static bool TryCreateSourceEnvelope(
        MessageCitation? citation,
        string? id,
        MessagesStreamMappingState state,
        out AIEventEnvelope envelope)
    {
        envelope = default!;

        if (citation?.Type != "web_search_result_location" || string.IsNullOrWhiteSpace(citation.Url))
            return false;

        var sourceId = citation.EncryptedIndex ?? citation.Url;
        if (!state.SeenSourceIds.Add(sourceId))
            return false;

        envelope = CreateEnvelope("source-url", id, new AISourceUrlEventData
        {
            SourceId = sourceId,
            Url = citation.Url,
            Title = citation.Title ?? citation.Url,
            Type = citation.Type
        });

        return true;
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

    private static MessagesUsage? MergeUsage(MessagesUsage? current, MessagesUsage? update)
    {
        if (update is null)
            return current;

        current ??= new MessagesUsage();
        current.InputTokens = update.InputTokens ?? current.InputTokens;
        current.OutputTokens = update.OutputTokens ?? current.OutputTokens;
        current.CacheCreationInputTokens = update.CacheCreationInputTokens ?? current.CacheCreationInputTokens;
        current.CacheReadInputTokens = update.CacheReadInputTokens ?? current.CacheReadInputTokens;
        current.ServiceTier ??= update.ServiceTier;
        current.InferenceGeo ??= update.InferenceGeo;
        current.CacheCreation ??= update.CacheCreation;
        current.ServerToolUse ??= update.ServerToolUse;
        return current;
    }
}
