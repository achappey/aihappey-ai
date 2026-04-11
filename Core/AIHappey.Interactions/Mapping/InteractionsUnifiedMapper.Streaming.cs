using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Interactions.Mapping;

public static partial class InteractionsUnifiedMapper
{
    public static IEnumerable<AIStreamEvent> ToUnifiedStreamEvent(this InteractionStreamEventPart part, string providerId)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        switch (part)
        {
            case InteractionContentStartEvent start when start.Content is InteractionTextContent:
                RememberStreamContentType(providerId, start.Index, "text");
                yield return CreateStreamEvent(
                    providerId,
                    new AIEventEnvelope
                    {
                        Type = "text-start",
                        Id = BuildContentEventId(start.Index),
                        Data = new AITextStartEventData()
                    },
                    part,
                    start.Index);
                yield break;

            case InteractionContentStartEvent { Content: InteractionThoughtContent thought } start:
                RememberStreamContentType(providerId, start.Index, "thought");
                RememberStreamThoughtSignature(providerId, start.Index, thought.Signature);
                yield return CreateStreamEvent(
                    providerId,
                    new AIEventEnvelope
                    {
                        Type = "reasoning-start",
                        Id = BuildContentEventId(start.Index),
                        Data = new AIReasoningStartEventData
                        {
                            ProviderMetadata = CreateThoughtSignatureProviderMetadata(providerId, thought.Signature)
                        }
                    },
                    part,
                    start.Index);
                yield break;

            case InteractionContentStartEvent start:
            {
                var unifiedContent = ToUnifiedContentParts([start.Content!], providerId).OfType<AIToolCallContentPart>().FirstOrDefault();
                if (unifiedContent is not null)
                {
                    if (HasMeaningfulValue(unifiedContent.Input))
                    {
                        yield return CreateStreamEvent(
                            providerId,
                            new AIEventEnvelope
                            {
                                Type = "tool-input-available",
                                Id = unifiedContent.ToolCallId,
                                Data = new AIToolInputAvailableEventData
                                {
                                    ToolName = unifiedContent.ToolName ?? "tool",
                                    Input = CloneIfJsonElement(unifiedContent.Input) ?? new { },
                                    ProviderExecuted = unifiedContent.ProviderExecuted,
                                    Title = unifiedContent.Title
                                }
                            },
                            part,
                            start.Index);
                    }

                    if (HasMeaningfulValue(unifiedContent.Output))
                    {
                        yield return CreateStreamEvent(
                            providerId,
                            new AIEventEnvelope
                            {
                                Type = "tool-output-available",
                                Id = unifiedContent.ToolCallId,
                                Data = new AIToolOutputAvailableEventData
                                {
                                    Output = CloneIfJsonElement(unifiedContent.Output) ?? new { },
                                    ProviderExecuted = unifiedContent.ProviderExecuted
                                }
                            },
                            part,
                            start.Index);
                    }

                    yield break;
                }

                yield return CreateStreamEvent(providerId, CreateDataEnvelope(part, start.Index), part, start.Index);
                yield break;
            }

            case InteractionContentDeltaEvent delta when string.Equals(delta.Delta?.Type, "text", StringComparison.OrdinalIgnoreCase):
                yield return CreateStreamEvent(
                    providerId,
                    new AIEventEnvelope
                    {
                        Type = "text-delta",
                        Id = BuildContentEventId(delta.Index),
                        Data = new AITextDeltaEventData
                        {
                            Delta = delta.Delta?.Text ?? string.Empty
                        }
                    },
                    part,
                    delta.Index);
                yield break;

            case InteractionContentDeltaEvent delta when string.Equals(delta.Delta?.Type, "thought_signature", StringComparison.OrdinalIgnoreCase)
                                                       || !string.IsNullOrWhiteSpace(GetThoughtSignature(delta)):
            {
                var signature = GetThoughtSignature(delta);
                RememberStreamContentType(providerId, delta.Index, "thought");
                RememberStreamThoughtSignature(providerId, delta.Index, signature);
                yield return CreateStreamEvent(
                    providerId,
                    new AIEventEnvelope
                    {
                        Type = "reasoning-delta",
                        Id = BuildContentEventId(delta.Index),
                        Data = new AIReasoningDeltaEventData
                        {
                            Delta = string.Empty,
                            ProviderMetadata = CreateThoughtSignatureProviderMetadata(providerId, signature)
                        }
                    },
                    part,
                    delta.Index);
                yield break;
            }

            case InteractionContentDeltaEvent delta when string.Equals(delta.Delta?.Type, "thought", StringComparison.OrdinalIgnoreCase):
                RememberStreamContentType(providerId, delta.Index, "thought");
                yield return CreateStreamEvent(
                    providerId,
                    new AIEventEnvelope
                    {
                        Type = "reasoning-delta",
                        Id = BuildContentEventId(delta.Index),
                        Data = new AIReasoningDeltaEventData
                        {
                            Delta = delta.Delta?.Text ?? string.Empty
                        }
                    },
                    part,
                    delta.Index);
                yield break;

            case InteractionContentDeltaEvent delta:
                yield return CreateStreamEvent(providerId, CreateDataEnvelope(part, delta.Index), part, delta.Index);
                yield break;

            case InteractionContentStopEvent stop:
            {
                var rememberedType = ForgetStreamContentType(providerId, stop.Index);
                var rememberedSignature = GetStreamThoughtSignature(providerId, stop.Index);

                if (string.Equals(rememberedType, "text", StringComparison.OrdinalIgnoreCase))
                {
                    yield return CreateStreamEvent(
                        providerId,
                        new AIEventEnvelope
                        {
                            Type = "text-end",
                            Id = BuildContentEventId(stop.Index),
                            Data = new AITextEndEventData()
                        },
                        part,
                        stop.Index);

                    yield break;
                }

                if (string.Equals(rememberedType, "thought", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(rememberedSignature))
                {
                    yield return CreateStreamEvent(
                        providerId,
                        new AIEventEnvelope
                        {
                            Type = "reasoning-end",
                            Id = BuildContentEventId(stop.Index),
                            Data = new AIReasoningEndEventData
                            {
                                ProviderMetadata = CreateThoughtSignatureProviderMetadata(
                                    providerId,
                                    ForgetStreamThoughtSignature(providerId, stop.Index) ?? rememberedSignature)
                            }
                        },
                        part,
                        stop.Index);

                    yield break;
                }

                yield return CreateStreamEvent(providerId, CreateDataEnvelope(part, stop.Index), part, stop.Index);
                yield break;
            }

            case InteractionCompleteEvent complete:
                yield return CreateStreamEvent(
                    providerId,
                    new AIEventEnvelope
                    {
                        Type = "finish",
                        Id = complete.Interaction?.Id ?? complete.EventId,
                        Data = new AIFinishEventData
                        {
                            Response = complete.Interaction,
                            Model = complete.Interaction?.Model ?? complete.Interaction?.Agent,
                            CompletedAt = complete.Interaction?.Updated,
                            FinishReason = complete.Interaction?.Status == "completed" ? "stop" : "other",
                            InputTokens = complete.Interaction?.Usage?.TotalInputTokens,
                            OutputTokens = complete.Interaction?.Usage?.TotalOutputTokens,
                            TotalTokens = complete.Interaction?.Usage?.TotalTokens
                        },
                        Metadata = new Dictionary<string, object?>
                        {
                            ["status"] = complete.Interaction?.Status,
                            ["id"] = complete.Interaction?.Id
                        }
                    },
                    part,
                    0);
                yield break;

            case InteractionErrorEvent error:
                yield return CreateStreamEvent(
                    providerId,
                    new AIEventEnvelope
                    {
                        Type = "error",
                        Id = error.EventId,
                        Data = new AIErrorEventData
                        {
                            ErrorText = error.Error?.Message ?? "Unknown interaction error"
                        },
                        Metadata = new Dictionary<string, object?>
                        {
                            ["code"] = error.Error?.Code
                        }
                    },
                    part,
                    0);
                yield break;

            default:
                yield return CreateStreamEvent(providerId, CreateDataEnvelope(part, 0), part, 0);
                yield break;
        }
    }

    public static InteractionStreamEventPart ToInteractionStreamEvent(this AIStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        var envelope = streamEvent.Event;
        var index = GetContentIndex(streamEvent);

        return envelope.Type switch
        {
            "text-start" => new InteractionContentStartEvent
            {
                Index = index,
                EventId = ExtractValue<string>(streamEvent.Metadata, "interactions.event_id"),
                Content = new InteractionTextContent()
            },
            "text-delta" => new InteractionContentDeltaEvent
            {
                Index = index,
                EventId = ExtractValue<string>(streamEvent.Metadata, "interactions.event_id"),
                Delta = new InteractionContentDeltaData
                {
                    Type = "text",
                    Text = DeserializeFromObject<AITextDeltaEventData>(envelope.Data)?.Delta
                }
            },
            "text-end" => new InteractionContentStopEvent
            {
                Index = index,
                EventId = ExtractValue<string>(streamEvent.Metadata, "interactions.event_id")
            },
            "reasoning-start" => new InteractionContentStartEvent
            {
                Index = index,
                EventId = ExtractValue<string>(streamEvent.Metadata, "interactions.event_id"),
                Content = new InteractionThoughtContent()
            },
            "reasoning-delta" => new InteractionContentDeltaEvent
            {
                Index = index,
                EventId = ExtractValue<string>(streamEvent.Metadata, "interactions.event_id"),
                Delta = CreateReasoningDeltaData(streamEvent.ProviderId, DeserializeFromObject<AIReasoningDeltaEventData>(envelope.Data))
            },
            "reasoning-end" => new InteractionContentStopEvent
            {
                Index = index,
                EventId = ExtractValue<string>(streamEvent.Metadata, "interactions.event_id")
            },
            "tool-input-available" => new InteractionContentStartEvent
            {
                Index = index,
                EventId = ExtractValue<string>(streamEvent.Metadata, "interactions.event_id"),
                Content = CreateInteractionToolContentFromInput(envelope.Data)
            },
            "tool-output-available" => new InteractionContentStartEvent
            {
                Index = index,
                EventId = ExtractValue<string>(streamEvent.Metadata, "interactions.event_id"),
                Content = CreateInteractionToolContentFromOutput(envelope.Data)
            },
            "finish" => new InteractionCompleteEvent
            {
                EventId = ExtractValue<string>(streamEvent.Metadata, "interactions.event_id"),
                Interaction = DeserializeFromObject<Interaction>(DeserializeFromObject<AIFinishEventData>(envelope.Data)?.Response)
                              ?? (envelope.Data is AIFinishEventData finish && finish.Model is not null
                                  ? new Interaction { Model = finish.Model, Status = finish.FinishReason }
                                  : new Interaction())
            },
            "error" => new InteractionErrorEvent
            {
                EventId = ExtractValue<string>(streamEvent.Metadata, "interactions.event_id"),
                Error = new InteractionErrorInfo
                {
                    Message = DeserializeFromObject<AIErrorEventData>(envelope.Data)?.ErrorText,
                    Code = ExtractValue<string>(envelope.Metadata is null ? null : envelope.Metadata.ToDictionary(a => a.Key, a => (object?)a.Value), "code")
                }
            },
            _ => new InteractionUnknownStreamEvent
            {
                EventType = ExtractValue<string>(streamEvent.Metadata, "interactions.source_event_type") ?? $"unmapped.{envelope.Type}"
            }
        };
    }

    private static AIStreamEvent CreateStreamEvent(string providerId, AIEventEnvelope envelope, InteractionStreamEventPart source, int index)
        => new()
        {
            ProviderId = providerId,
            Event = envelope,
            Metadata = new Dictionary<string, object?>
            {
                ["interactions.source_event_type"] = source.EventType,
                ["interactions.content.index"] = index,
                ["interactions.event_id"] = source switch
                {
                    InteractionStartEvent start => start.EventId,
                    InteractionCompleteEvent complete => complete.EventId,
                    InteractionStatusUpdateEvent status => status.EventId,
                    InteractionContentStartEvent contentStart => contentStart.EventId,
                    InteractionContentDeltaEvent contentDelta => contentDelta.EventId,
                    InteractionContentStopEvent contentStop => contentStop.EventId,
                    InteractionErrorEvent error => error.EventId,
                    _ => null
                }
            }
        };

    private static AIEventEnvelope CreateDataEnvelope(InteractionStreamEventPart source, int index)
        => new()
        {
            Type = $"data-interactions.{source.EventType}",
            Id = source switch
            {
                InteractionStartEvent start => start.EventId,
                InteractionCompleteEvent complete => complete.EventId,
                InteractionStatusUpdateEvent status => status.EventId,
                InteractionContentStartEvent contentStart => contentStart.EventId,
                InteractionContentDeltaEvent contentDelta => contentDelta.EventId,
                InteractionContentStopEvent contentStop => contentStop.EventId,
                InteractionErrorEvent error => error.EventId,
                _ => null
            },
            Data = new AIDataEventData
            {
                Id = BuildContentEventId(index),
                Data = JsonSerializer.SerializeToElement(source, Json)
            }
        };

    private static InteractionContent CreateInteractionToolContentFromInput(object? data)
    {
        var payload = DeserializeFromObject<AIToolInputAvailableEventData>(data);
        var input = CloneIfJsonElement(payload?.Input);
        return new InteractionFunctionCallContent
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = payload?.ToolName,
            Arguments = input
        };
    }

    private static InteractionContent CreateInteractionToolContentFromOutput(object? data)
    {
        var payload = DeserializeFromObject<AIToolOutputAvailableEventData>(data);
        return new InteractionFunctionResultContent
        {
            CallId = Guid.NewGuid().ToString("N"),
            Result = CloneIfJsonElement(payload?.Output)
        };
    }

    private static InteractionContentDeltaData CreateReasoningDeltaData(string providerId, AIReasoningDeltaEventData? data)
    {
        if (TryGetThoughtSignatureProviderMetadata(data, providerId, out var signature))
        {
            return new InteractionContentDeltaData
            {
                Type = "thought_signature",
                AdditionalProperties = new Dictionary<string, JsonElement>
                {
                    ["signature"] = JsonSerializer.SerializeToElement(signature, Json)
                }
            };
        }

        return new InteractionContentDeltaData
        {
            Type = "thought",
            Text = data?.Delta
        };
    }
}
