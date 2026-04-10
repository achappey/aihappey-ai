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
                        ProviderExecuted = IsProviderExecutedTool(part.ContentBlock.Type)
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
                    yield return CreateEnvelope("tool-input-available", stopState.EventId, new AIToolInputAvailableEventData
                    {
                        ToolName = stopState.Block.Name ?? stopState.BlockType,
                        Title = stopState.Block.Name,
                        ProviderExecuted = IsProviderExecutedTool(stopState.BlockType),
                        Input = ParseToolInput(stopState) ?? JsonSerializer.SerializeToElement(new { }, Json)
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
                Data = data ?? new { }
            },
            Timestamp = DateTimeOffset.UtcNow
        };

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
}
