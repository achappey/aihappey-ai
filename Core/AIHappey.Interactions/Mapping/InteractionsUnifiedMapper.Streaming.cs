using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Interactions.Mapping;

public static partial class InteractionsUnifiedMapper
{
    public static AIStreamEvent ToUnifiedRequestStreamEvent(
        this InteractionRequest request,
        string providerId,
        Dictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        return new AIStreamEvent
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "data-interactions.request",
                Timestamp = DateTimeOffset.UtcNow,
                Data = new AIDataEventData
                {
                    Data = JsonSerializer.SerializeToElement(request, Json)
                }
            },
            Metadata = headers is null || headers.Count == 0
                ? null
                : new Dictionary<string, object?>
                {
                    ["unified.request.headers"] = headers.ToDictionary(a => a.Key, a => (object?)a.Value)
                }
        };
    }

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

            case InteractionContentStartEvent { Content: InteractionImageContent image } start:
                {
                    RememberStreamContentType(providerId, start.Index, "image");
                    RememberStreamImageStart(providerId, start.Index, image.MimeType);

                    yield return CreateStreamEvent(
                        providerId,
                        new AIEventEnvelope
                        {
                            Type = "tool-input-available",
                            Id = BuildImageToolCallId(start.Index),
                            Data = new AIToolInputAvailableEventData
                            {
                                ToolName = "image",
                                Input = new
                                {
                                },
                                ProviderExecuted = true,
                                Title = "image",
                                ProviderMetadata = CreateInteractionImageToolProviderMetadata(providerId, start.Index, image.MimeType)
                            }
                        },
                        part,
                        start.Index);

                    yield break;
                }

            case InteractionContentStartEvent { Content: InteractionThoughtContent thought } start:
                {
                    var hasSummaryText = !string.IsNullOrWhiteSpace(FlattenContentText(thought.Summary));
                    RememberStreamContentType(providerId, start.Index, hasSummaryText ? "thought" : "thought_signature_only");
                    RememberStreamThoughtHasText(providerId, start.Index, hasSummaryText);
                    RememberStreamThoughtSignature(providerId, start.Index, thought.Signature);

                    if (hasSummaryText)
                    {
                        RememberOpenThoughtAnchor(providerId, start.Index);
                    }
                    else if (GetOpenThoughtAnchor(providerId) is { } anchorIndex && anchorIndex != start.Index)
                    {
                        RememberStreamThoughtSignature(providerId, anchorIndex, thought.Signature);
                        yield break;
                    }

                    yield return CreateStreamEvent(
                        providerId,
                        new AIEventEnvelope
                        {
                            Type = "reasoning-start",
                            Id = BuildContentEventId(start.Index),
                            Data = new AIReasoningStartEventData
                            {
                                Signature = thought.Signature,
                                ProviderMetadata = CreateThoughtSignatureProviderMetadata(providerId, thought.Signature)
                            }
                        },
                        part,
                        start.Index);
                    yield break;
                }

            case InteractionContentStartEvent start:
                {
                    var unifiedContent = ToUnifiedContentParts([start.Content!], providerId).OfType<AIToolCallContentPart>().FirstOrDefault();
                    if (unifiedContent is not null)
                    {
                        var emittedToolEvent = false;

                        if (HasMeaningfulValue(unifiedContent.Input))
                        {
                            emittedToolEvent = true;
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
                                        Title = unifiedContent.Title,
                                        ProviderMetadata = GetProviderScopedMetadataEnvelope(unifiedContent.Metadata, providerId)
                                    }
                                },
                                part,
                                start.Index);
                        }

                        if (HasMeaningfulValue(unifiedContent.Output))
                        {
                            emittedToolEvent = true;
                            yield return CreateStreamEvent(
                                providerId,
                                new AIEventEnvelope
                                {
                                    Type = "tool-output-available",
                                    Id = unifiedContent.ToolCallId,
                                    Data = new AIToolOutputAvailableEventData
                                    {
                                        ToolName = unifiedContent.ToolName,
                                        Output = CloneIfJsonElement(unifiedContent.Output) ?? new { },
                                        ProviderExecuted = unifiedContent.ProviderExecuted,
                                        ProviderMetadata = GetProviderScopedMetadataEnvelope(unifiedContent.Metadata, providerId)
                                    }
                                },
                                part,
                                start.Index);
                        }

                        if (emittedToolEvent)
                            yield break;
                    }

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

            case InteractionContentDeltaEvent delta when string.Equals(delta.Delta?.Type, "image", StringComparison.OrdinalIgnoreCase):
                {
                    RememberStreamContentType(providerId, delta.Index, "image");

                    var mimeType = GetDeltaAdditionalString(delta, "mime_type")
                                   ?? GetStreamImage(providerId, delta.Index)?.MimeType
                                   ?? "image/png";
                    var imageData = GetDeltaAdditionalString(delta, "data") ?? delta.Delta?.Text;
                    var imageState = RememberStreamImageDelta(providerId, delta.Index, mimeType, imageData);

                    if (TryCreateInteractionImageToolResult(imageState, out var preliminaryImageOutput))
                    {
                        yield return CreateStreamEvent(
                            providerId,
                            new AIEventEnvelope
                            {
                                Type = "tool-output-available",
                                Id = imageState.ToolCallId,
                                Data = new AIToolOutputAvailableEventData
                                {
                                    ToolName = "image",
                                    Output = preliminaryImageOutput,
                                    ProviderExecuted = true,
                                    Preliminary = true,
                                    ProviderMetadata = CreateInteractionImageToolProviderMetadata(providerId, delta.Index, mimeType)
                                }
                            },
                            part,
                            delta.Index);
                    }

                    yield break;
                }

            case InteractionContentDeltaEvent delta when string.Equals(delta.Delta?.Type, "thought_signature", StringComparison.OrdinalIgnoreCase)
                                                       || !string.IsNullOrWhiteSpace(GetThoughtSignature(delta)):
                {
                    var signature = GetThoughtSignature(delta);
                    var targetIndex = GetStreamThoughtHasText(providerId, delta.Index) || GetOpenThoughtAnchor(providerId) is not { } anchorIndex || anchorIndex == delta.Index
                        ? delta.Index
                        : anchorIndex;

                    RememberStreamContentType(providerId, delta.Index, GetStreamThoughtHasText(providerId, delta.Index) ? "thought" : "thought_signature_only");
                    RememberStreamThoughtSignature(providerId, delta.Index, signature);
                    RememberStreamThoughtSignature(providerId, targetIndex, signature);
                    yield break;
                }

            case InteractionContentDeltaEvent delta when string.Equals(delta.Delta?.Type, "thought_summary", StringComparison.OrdinalIgnoreCase):
                {
                    var summaryText = GetThoughtSummaryText(delta);
                    RememberStreamContentType(providerId, delta.Index, "thought");
                    RememberStreamThoughtHasText(providerId, delta.Index, !string.IsNullOrWhiteSpace(summaryText));
                    if (!string.IsNullOrWhiteSpace(summaryText))
                        RememberOpenThoughtAnchor(providerId, delta.Index);
                    yield return CreateStreamEvent(
                        providerId,
                        new AIEventEnvelope
                        {
                            Type = "reasoning-delta",
                            Id = BuildContentEventId(delta.Index),
                            Data = new AIReasoningDeltaEventData
                            {
                                Delta = summaryText ?? string.Empty,
                                Signature = GetStreamThoughtSignature(providerId, delta.Index),
                                ProviderMetadata = CreateThoughtSignatureProviderMetadata(
                                    providerId,
                                    GetStreamThoughtSignature(providerId, delta.Index))
                            }
                        },
                        part,
                        delta.Index);
                    yield break;
                }

            case InteractionContentDeltaEvent delta when string.Equals(delta.Delta?.Type, "thought", StringComparison.OrdinalIgnoreCase):
                RememberStreamContentType(providerId, delta.Index, "thought");
                yield break;

            case InteractionContentDeltaEvent delta when string.Equals(delta.Delta?.Type, "code_execution_call", StringComparison.OrdinalIgnoreCase):
                {
                    var toolCallId = GetDeltaAdditionalString(delta, "id")
                                     ?? GetDeltaAdditionalString(delta, "call_id")
                                     ?? BuildContentEventId(delta.Index);
                    var toolName = "code_execution";
                    var input = GetDeltaAdditionalObject(delta, "arguments") ?? JsonSerializer.SerializeToElement(new { }, Json);

                    yield return CreateStreamEvent(
                        providerId,
                        new AIEventEnvelope
                        {
                            Type = "tool-input-available",
                            Id = toolCallId,
                            Data = new AIToolInputAvailableEventData
                            {
                                ToolName = toolName,
                                Input = CloneIfJsonElement(input) ?? new { },
                                ProviderExecuted = true,
                                Title = toolName
                            }
                        },
                        part,
                        delta.Index);

                    yield break;
                }

            case InteractionContentDeltaEvent delta when string.Equals(delta.Delta?.Type, "code_execution_result", StringComparison.OrdinalIgnoreCase):
                {
                    var toolCallId = GetDeltaAdditionalString(delta, "call_id")
                                     ?? GetDeltaAdditionalString(delta, "id")
                                     ?? BuildContentEventId(delta.Index);
                    var resultText = GetDeltaAdditionalString(delta, "result") ?? delta.Delta?.Text ?? string.Empty;
                    var isError = GetDeltaAdditionalBool(delta, "is_error");

                    yield return CreateStreamEvent(
                        providerId,
                        new AIEventEnvelope
                        {
                            Type = "tool-output-available",
                            Id = toolCallId,
                            Data = new AIToolOutputAvailableEventData
                            {
                                ToolName = "code_execution",
                                Output = CreateCodeExecutionToolResultPayload(resultText),
                                ProviderExecuted = true,
                                ProviderMetadata = CreateCodeExecutionToolOutputProviderMetadata(providerId, toolCallId, isError)
                            }
                        },
                        part,
                        delta.Index);

                    yield break;
                }

            case InteractionContentDeltaEvent delta when string.Equals(delta.Delta?.Type, "google_search_call", StringComparison.OrdinalIgnoreCase):
                {
                    var toolCallId = GetDeltaAdditionalString(delta, "id")
                                     ?? GetDeltaAdditionalString(delta, "call_id")
                                     ?? BuildContentEventId(delta.Index);
                    var toolName = "google_search";
                    var input = GetDeltaAdditionalObject(delta, "arguments") ?? JsonSerializer.SerializeToElement(new { }, Json);
                    var providerMetadata = CreateGoogleSearchToolProviderMetadata(
                        providerId,
                        searchType: GetDeltaAdditionalString(delta, "search_type"));

                    yield return CreateStreamEvent(
                        providerId,
                        new AIEventEnvelope
                        {
                            Type = "tool-input-available",
                            Id = toolCallId,
                            Data = new AIToolInputAvailableEventData
                            {
                                ToolName = toolName,
                                Input = CloneIfJsonElement(input) ?? new { },
                                ProviderExecuted = true,
                                Title = toolName,
                                ProviderMetadata = providerMetadata
                            }
                        },
                        part,
                        delta.Index);

                    yield break;
                }

            case InteractionContentDeltaEvent delta when string.Equals(delta.Delta?.Type, "google_search_result", StringComparison.OrdinalIgnoreCase):
                {
                    var toolCallId = GetDeltaAdditionalString(delta, "call_id")
                                     ?? GetDeltaAdditionalString(delta, "id")
                                     ?? BuildContentEventId(delta.Index);
                    var resultPayload = GetDeltaAdditionalObject(delta, "result")
                                        ?? JsonSerializer.SerializeToElement(Array.Empty<InteractionGoogleSearchResult>(), Json);
                    var structuredContent = JsonSerializer.SerializeToElement(new
                    {
                        content = CloneIfJsonElement(resultPayload) ?? resultPayload,
                        result = CloneIfJsonElement(resultPayload) ?? resultPayload
                    }, Json);
                    var providerMetadata = CreateGoogleSearchToolProviderMetadata(
                        providerId,
                        toolCallId,
                        "google_search_result",
                        isError: GetDeltaAdditionalBool(delta, "is_error"));

                    yield return CreateStreamEvent(
                        providerId,
                        new AIEventEnvelope
                        {
                            Type = "tool-output-available",
                            Id = toolCallId,
                            Data = new AIToolOutputAvailableEventData
                            {
                                Output = new CallToolResult
                                {
                                    StructuredContent = structuredContent.Clone()
                                },
                                ProviderExecuted = true,
                                ProviderMetadata = providerMetadata
                            }
                        },
                        part,
                        delta.Index);

                    yield break;
                }

            case InteractionContentDeltaEvent delta when string.Equals(delta.Delta?.Type, "google_maps_call", StringComparison.OrdinalIgnoreCase):
                {
                    var toolCallId = GetDeltaAdditionalString(delta, "id")
                                     ?? GetDeltaAdditionalString(delta, "call_id")
                                     ?? BuildContentEventId(delta.Index);
                    var toolName = "google_maps";
                    var input = GetDeltaAdditionalObject(delta, "arguments") ?? JsonSerializer.SerializeToElement(new { }, Json);
                    var providerMetadata = CreateGoogleMapsToolProviderMetadata(
                        providerId);

                    yield return CreateStreamEvent(
                        providerId,
                        new AIEventEnvelope
                        {
                            Type = "tool-input-available",
                            Id = toolCallId,
                            Data = new AIToolInputAvailableEventData
                            {
                                ToolName = toolName,
                                Input = CloneIfJsonElement(input) ?? new { },
                                ProviderExecuted = true,
                                Title = toolName,
                                ProviderMetadata = providerMetadata
                            }
                        },
                        part,
                        delta.Index);

                    yield break;
                }

            case InteractionContentDeltaEvent delta when string.Equals(delta.Delta?.Type, "google_maps_result", StringComparison.OrdinalIgnoreCase):
                {
                    var toolCallId = GetDeltaAdditionalString(delta, "call_id")
                                     ?? GetDeltaAdditionalString(delta, "id")
                                     ?? BuildContentEventId(delta.Index);
                    var resultPayload = GetDeltaAdditionalObject(delta, "result")
                                        ?? JsonSerializer.SerializeToElement(Array.Empty<InteractionGoogleMapsResult>(), Json);
                    var structuredContent = CloneIfJsonElement(resultPayload) ?? JsonSerializer.SerializeToElement(Array.Empty<InteractionGoogleMapsResult>(), Json);
                    var providerMetadata = CreateGoogleMapsToolProviderMetadata(
                        providerId);

                    yield return CreateStreamEvent(
                        providerId,
                        new AIEventEnvelope
                        {
                            Type = "tool-output-available",
                            Id = toolCallId,
                            Data = new AIToolOutputAvailableEventData
                            {
                                Output = new CallToolResult
                                {
                                    StructuredContent = (JsonElement)structuredContent
                                },
                                ProviderExecuted = true,
                                ProviderMetadata = providerMetadata
                            }
                        },
                        part,
                        delta.Index);

                    foreach (var sourceEvent in CreateGoogleMapsSourceUrlEvents(providerId, delta, toolCallId, resultPayload))
                        yield return sourceEvent;

                    yield break;
                }

            case InteractionContentDeltaEvent delta when HasSourceUrlAnnotations(delta):
                {
                    var annotations = GetSourceUrlAnnotations(delta);
                    for (var i = 0; i < annotations.Count; i++)
                    {
                        var annotation = annotations[i];
                        if (string.IsNullOrWhiteSpace(annotation.Url))
                            continue;

                        yield return CreateStreamEvent(
                            providerId,
                            new AIEventEnvelope
                            {
                                Type = "source-url",
                                Id = BuildContentEventId(delta.Index),
                                Data = new AISourceUrlEventData
                                {
                                    SourceId = BuildCitationSourceId(delta, i, annotation),
                                    Url = annotation.Url,
                                    StartIndex = annotation.StartIndex,
                                    EndIndex = annotation.EndIndex,
                                    Title = GetAnnotationTitle(annotation),
                                    Type = annotation.Type,
                                    ProviderMetadata = CreateAnnotationProviderMetadata(providerId, delta, annotation)
                                }
                            },
                            part,
                            delta.Index);
                    }

                    yield break;
                }

            case InteractionContentDeltaEvent delta:
                yield break;

            case InteractionContentStopEvent stop:
                {
                    var rememberedType = ForgetStreamContentType(providerId, stop.Index);
                    var rememberedSignature = GetStreamThoughtSignature(providerId, stop.Index);
                    var rememberedHasText = ForgetStreamThoughtHasText(providerId, stop.Index);
                    var rememberedImage = ForgetStreamImage(providerId, stop.Index);

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
                        || string.Equals(rememberedType, "thought_signature_only", StringComparison.OrdinalIgnoreCase)
                        || !string.IsNullOrWhiteSpace(rememberedSignature))
                    {
                        if (!rememberedHasText
                            && GetOpenThoughtAnchor(providerId) is { } anchorIndex
                            && anchorIndex != stop.Index)
                        {
                            ForgetStreamThoughtSignature(providerId, stop.Index);
                            yield break;
                        }

                        if (GetOpenThoughtAnchor(providerId) == stop.Index)
                            ForgetOpenThoughtAnchor(providerId);

                        var signature = ForgetStreamThoughtSignature(providerId, stop.Index) ?? rememberedSignature;

                        yield return CreateStreamEvent(
                            providerId,
                            new AIEventEnvelope
                            {
                                Type = "reasoning-end",
                                Id = BuildContentEventId(stop.Index),
                                Data = new AIReasoningEndEventData
                                {
                                    Signature = signature,
                                    ProviderMetadata = CreateThoughtSignatureProviderMetadata(
                                        providerId,
                                        signature)
                                }
                            },
                            part,
                            stop.Index);

                        yield break;
                    }

                    if (string.Equals(rememberedType, "image", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryCreateInteractionImageToolResult(rememberedImage, out var finalImageOutput))
                        {
                            yield return CreateStreamEvent(
                                providerId,
                                new AIEventEnvelope
                                {
                                    Type = "tool-output-available",
                                    Id = rememberedImage?.ToolCallId ?? BuildImageToolCallId(stop.Index),
                                    Data = new AIToolOutputAvailableEventData
                                    {
                                        ToolName = "image",
                                        Output = finalImageOutput,
                                        ProviderExecuted = true,
                                        Preliminary = false,
                                        ProviderMetadata = CreateInteractionImageToolProviderMetadata(providerId, stop.Index, rememberedImage?.MimeType)
                                    }
                                },
                                part,
                                stop.Index);
                        }

                        yield break;
                    }

                    yield break;
                }

            case InteractionCompleteEvent complete:
                ForgetOpenThoughtAnchor(providerId);
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
                ForgetOpenThoughtAnchor(providerId);
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
                Content = CreateInteractionToolContentFromInput(envelope.Id, envelope.Data)
            },
            "tool-output-available" => new InteractionContentStartEvent
            {
                Index = index,
                EventId = ExtractValue<string>(streamEvent.Metadata, "interactions.event_id"),
                Content = CreateInteractionToolContentFromOutput(envelope.Id, envelope.Data)
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

    private static AIEventEnvelope CreateResponseDataEnvelope(Interaction response)
        => new()
        {
            Type = "data-interactions.response",
            Id = response.Id,
            Data = new AIDataEventData
            {
                Id = response.Id,
                Data = JsonSerializer.SerializeToElement(response, Json)
            },
            Timestamp = DateTimeOffset.UtcNow
        };

    private static InteractionContent CreateInteractionToolContentFromInput(string? toolCallId, object? data)
    {
        var payload = DeserializeFromObject<AIToolInputAvailableEventData>(data);
        var input = CloneIfJsonElement(payload?.Input);
        var toolName = payload?.ToolName;
        var contentType = InferInteractionToolContentType(toolName, payload?.ProviderExecuted, hasOutput: false);
        var signature = TryGetProviderMetadataString(payload?.ProviderMetadata, "signature");
        var serverName = TryGetProviderMetadataString(payload?.ProviderMetadata, "server_name");
        var searchType = TryGetProviderMetadataString(payload?.ProviderMetadata, "search_type");
        var id = toolCallId ?? Guid.NewGuid().ToString("N");

        if (string.Equals(contentType, "google_search_call", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractionGoogleSearchCallContent
            {
                Id = id,
                Signature = signature,
                SearchType = searchType,
                Arguments = DeserializeFromObject<InteractionGoogleSearchCallArguments>(input)
            };
        }

        if (string.Equals(contentType, "google_maps_call", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractionGoogleMapsCallContent
            {
                Id = id,
                Signature = signature,
                Arguments = DeserializeFromObject<InteractionGoogleMapsCallArguments>(input)
            };
        }

        if (string.Equals(contentType, "code_execution_call", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractionCodeExecutionCallContent
            {
                Id = id,
                Signature = signature,
                Arguments = DeserializeFromObject<InteractionCodeExecutionCallArguments>(input)
            };
        }

        if (string.Equals(contentType, "url_context_call", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractionUrlContextCallContent
            {
                Id = id,
                Signature = signature,
                Arguments = DeserializeFromObject<InteractionUrlContextCallArguments>(input)
            };
        }

        if (string.Equals(contentType, "file_search_call", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractionFileSearchCallContent
            {
                Id = id,
                Signature = signature
            };
        }

        if (string.Equals(contentType, "mcp_server_tool_call", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractionMcpServerToolCallContent
            {
                Id = id,
                Signature = signature,
                Name = toolName,
                ServerName = serverName,
                Arguments = input
            };
        }

        return new InteractionFunctionCallContent
        {
            Id = id,
            Name = toolName,
            Signature = signature,
            Arguments = input
        };
    }

    private static InteractionContent CreateInteractionToolContentFromOutput(string? toolCallId, object? data)
    {
        var payload = DeserializeFromObject<AIToolOutputAvailableEventData>(data);
        var toolName = payload?.ToolName;
        var contentType = InferInteractionToolContentType(toolName, payload?.ProviderExecuted, hasOutput: true);
        var signature = TryGetProviderMetadataString(payload?.ProviderMetadata, "signature");
        var isError = ExtractProviderMetadataBool(payload?.ProviderMetadata, "is_error");
        var serverName = TryGetProviderMetadataString(payload?.ProviderMetadata, "server_name");
        var callId = toolCallId ?? Guid.NewGuid().ToString("N");

        if (string.Equals(contentType, "google_search_result", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractionGoogleSearchResultContent
            {
                CallId = callId,
                Signature = signature,
                IsError = isError,
                Result = DeserializeFromObject<List<InteractionGoogleSearchResult>>(TryGetStructuredContentResult(payload?.Output))
            };
        }

        if (string.Equals(contentType, "google_maps_result", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractionGoogleMapsResultContent
            {
                CallId = callId,
                Signature = signature,
                Result = DeserializeFromObject<List<InteractionGoogleMapsResult>>(TryGetStructuredContentResult(payload?.Output))
            };
        }

        if (string.Equals(contentType, "code_execution_result", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractionCodeExecutionResultContent
            {
                CallId = callId,
                Signature = signature,
                IsError = isError,
                Result = SerializePayload(payload?.Output, string.Empty)
            };
        }

        if (string.Equals(contentType, "url_context_result", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractionUrlContextResultContent
            {
                CallId = callId,
                Signature = signature,
                IsError = isError,
                Result = DeserializeFromObject<List<InteractionUrlContextResult>>(TryGetStructuredContentResult(payload?.Output) ?? payload?.Output)
            };
        }

        if (string.Equals(contentType, "file_search_result", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractionFileSearchResultContent
            {
                CallId = callId,
                Signature = signature,
                Result = DeserializeFromObject<List<InteractionFileSearchResult>>(TryGetStructuredContentResult(payload?.Output) ?? payload?.Output)
            };
        }

        if (string.Equals(contentType, "mcp_server_tool_result", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractionMcpServerToolResultContent
            {
                CallId = callId,
                Signature = signature,
                Name = toolName,
                ServerName = serverName,
                Result = CloneIfJsonElement(TryGetStructuredContentResult(payload?.Output) ?? payload?.Output)
            };
        }

        return new InteractionFunctionResultContent
        {
            CallId = callId,
            Name = toolName,
            Signature = signature,
            IsError = isError,
            Result = CloneIfJsonElement(TryGetStructuredContentResult(payload?.Output) ?? payload?.Output)
        };
    }

    private static InteractionContentDeltaData CreateReasoningDeltaData(string providerId, AIReasoningDeltaEventData? data)
    {
        var signature = data?.Signature ?? ExtractThoughtSignatureFromProviderMetadata(data?.ProviderMetadata, providerId);

        if (string.IsNullOrWhiteSpace(data?.Delta)
            && !string.IsNullOrWhiteSpace(signature))
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
            Type = "thought_summary",
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["signature"] = JsonSerializer.SerializeToElement(
                    signature,
                    Json),
                ["content"] = JsonSerializer.SerializeToElement(new InteractionTextContent
                {
                    Text = data?.Delta
                }, Json)
            }
        };
    }

    private static string? GetThoughtSummaryText(InteractionContentDeltaEvent delta)
    {
        if (delta.Delta?.AdditionalProperties is not null
            && delta.Delta.AdditionalProperties.TryGetValue("content", out var contentValue))
        {
            if (contentValue.ValueKind == JsonValueKind.String)
                return contentValue.GetString();

            if (contentValue.ValueKind == JsonValueKind.Object
                && contentValue.TryGetProperty("text", out var textValue))
            {
                return textValue.ValueKind == JsonValueKind.String
                    ? textValue.GetString()
                    : textValue.ToString();
            }
        }

        return delta.Delta?.Text;
    }

    private static Dictionary<string, Dictionary<string, object>>? CreateGoogleSearchToolProviderMetadata(
        string providerId,
        string? signature = null,
        string? searchType = null,
        bool? isError = null)
    {
        var metadata = CreateToolProviderMetadata(signature, isError, null, searchType);
        return metadata.Count == 0
            ? null
            : CreateProviderScopedMetadata(providerId, metadata.Where(a => a.Value is not null).ToDictionary(a => a.Key, a => ConvertProviderMetadataValue(a.Value)!));
    }

    private static Dictionary<string, Dictionary<string, object>>? CreateGoogleMapsToolProviderMetadata(
        string providerId,
        string? signature = null)
    {
        var metadata = CreateToolProviderMetadata(signature);
        return metadata.Count == 0
            ? null
            : CreateProviderScopedMetadata(providerId, metadata.Where(a => a.Value is not null).ToDictionary(a => a.Key, a => ConvertProviderMetadataValue(a.Value)!));
    }

    private static Dictionary<string, Dictionary<string, object>> CreateCodeExecutionToolOutputProviderMetadata(
        string providerId,
        string toolCallId,
        bool? isError = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["type"] = "code_execution_tool_result",
            ["tool_name"] = "code_execution",
            ["title"] = "code_execution",
            ["tool_use_id"] = toolCallId
        };

        if (isError is not null)
            metadata["is_error"] = isError.Value;

        return new Dictionary<string, Dictionary<string, object>>
        {
            [providerId] = metadata
        };
    }

    private static JsonElement CreateCodeExecutionToolResultPayload(string resultText)
        => JsonSerializer.SerializeToElement(new
        {
            type = "code_execution_result",
            stdout = resultText,
            stderr = string.Empty,
            return_code = 0,
            content = Array.Empty<object>()
        }, Json);

    private static IEnumerable<AIStreamEvent> CreateGoogleMapsSourceUrlEvents(
        string providerId,
        InteractionContentDeltaEvent delta,
        string toolCallId,
        object? resultPayload)
    {
        var results = DeserializeFromObject<List<InteractionGoogleMapsResult>>(resultPayload);
        if (results is null || results.Count == 0)
            yield break;

        for (var resultIndex = 0; resultIndex < results.Count; resultIndex++)
        {
            var result = results[resultIndex];
            var places = result.Places;
            if (places is null || places.Count == 0)
                continue;

            for (var placeIndex = 0; placeIndex < places.Count; placeIndex++)
            {
                var place = places[placeIndex];
                if (string.IsNullOrWhiteSpace(place.Url))
                    continue;

                yield return CreateStreamEvent(
                    providerId,
                    new AIEventEnvelope
                    {
                        Type = "source-url",
                        Id = toolCallId,
                        Data = new AISourceUrlEventData
                        {
                            SourceId = BuildGoogleMapsSourceId(toolCallId, resultIndex, placeIndex, place),
                            Url = place.Url,
                            Title = place.Name,
                            Type = "google_maps_result",
                            ProviderMetadata = CreateGoogleMapsPlaceSourceMetadata(providerId, toolCallId, resultIndex, placeIndex, result, place)
                        }
                    },
                    delta,
                    delta.Index);
            }
        }
    }

    private static Dictionary<string, Dictionary<string, object>> CreateGoogleMapsPlaceSourceMetadata(
        string providerId,
        string toolCallId,
        int resultIndex,
        int placeIndex,
        InteractionGoogleMapsResult result,
        InteractionPlace place)
    {
        var metadata = new Dictionary<string, object>
        {
            ["type"] = "google_maps_result",
            ["tool_use_id"] = toolCallId,
            ["tool_name"] = "google_maps",
            ["interactions.content.index"] = resultIndex,
            ["google_maps.result_index"] = resultIndex,
            ["google_maps.place_index"] = placeIndex,
            ["google_maps.place_id"] = place.PlaceId ?? string.Empty,
            ["google_maps.place_name"] = place.Name ?? string.Empty
        };

        if (!string.IsNullOrWhiteSpace(result.WidgetContextToken))
            metadata["google_maps.widget_context_token"] = result.WidgetContextToken;

        if (result.AdditionalProperties is not null)
        {
            foreach (var property in result.AdditionalProperties)
                metadata[$"google_maps.result.{property.Key}"] = property.Value.Clone();
        }

        if (place.AdditionalProperties is not null)
        {
            foreach (var property in place.AdditionalProperties)
                metadata[$"google_maps.place.{property.Key}"] = property.Value.Clone();
        }

        return new Dictionary<string, Dictionary<string, object>>
        {
            [providerId] = metadata
        };
    }

    private static Dictionary<string, Dictionary<string, object>> CreateInteractionImageToolProviderMetadata(
        string providerId,
        int index,
        string? mimeType)
        => new()
        {
            [providerId] = new Dictionary<string, object>
            {
                ["type"] = "interaction_image_stream",
                ["interactions.synthetic_image_tool"] = true,
                ["interactions.content.type"] = "image",
                ["interactions.content.index"] = index,
                ["mime_type"] = mimeType ?? "image/png"
            }
        };

    private static Dictionary<string, Dictionary<string, object>> CreateInteractionImageFileProviderMetadata(
        string providerId,
        int index,
        string? mimeType)
        => new()
        {
            [providerId] = new Dictionary<string, object>
            {
                ["type"] = "interaction_image_file",
                ["interactions.content.type"] = "image",
                ["interactions.content.index"] = index,
                ["mime_type"] = mimeType ?? "image/png"
            }
        };

    private static bool HasSourceUrlAnnotations(InteractionContentDeltaEvent delta)
        => GetSourceUrlAnnotations(delta).Count != 0;

    private static List<InteractionAnnotation> GetSourceUrlAnnotations(InteractionContentDeltaEvent delta)
    {
        if (delta.Delta?.AdditionalProperties is null
            || !delta.Delta.AdditionalProperties.TryGetValue("annotations", out var annotations)
            || annotations.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        try
        {
            return [.. (JsonSerializer.Deserialize<List<InteractionAnnotation>>(annotations.GetRawText(), Json) ?? []).Where(annotation => !string.IsNullOrWhiteSpace(annotation.Url))];
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<string, Dictionary<string, object>>? CreateAnnotationProviderMetadata(
        string providerId,
        InteractionContentDeltaEvent delta,
        InteractionAnnotation annotation)
    {
        var metadata = new Dictionary<string, object>
        {
            ["type"] = annotation.Type ?? "url_citation",
            ["start_index"] = annotation.StartIndex ?? -1,
            ["end_index"] = annotation.EndIndex ?? -1,
            ["interactions.content.index"] = delta.Index,
            ["interactions.event_id"] = delta.EventId ?? string.Empty
        };

        if (annotation.AdditionalProperties is not null)
        {
            foreach (var property in annotation.AdditionalProperties)
            {
                metadata[property.Key] = property.Value.Clone();
            }
        }

        return CreateProviderScopedMetadata(providerId, metadata);
    }

    private static string? GetAnnotationTitle(InteractionAnnotation annotation)
    {
        if (!string.IsNullOrWhiteSpace(annotation.Title))
            return annotation.Title;

        if (annotation.AdditionalProperties is null
            || !annotation.AdditionalProperties.TryGetValue("name", out var nameValue))
        {
            return null;
        }

        return nameValue.ValueKind == JsonValueKind.String
            ? nameValue.GetString()
            : nameValue.ToString();
    }

    private static string BuildCitationSourceId(InteractionContentDeltaEvent delta, int ordinal, InteractionAnnotation annotation)
        => $"{delta.EventId ?? BuildContentEventId(delta.Index)}:{delta.Index}:{ordinal}:{annotation.StartIndex ?? -1}:{annotation.EndIndex ?? -1}";

    private static string BuildGoogleMapsSourceId(string toolCallId, int resultIndex, int placeIndex, InteractionPlace place)
        => $"{toolCallId}:{resultIndex}:{placeIndex}:{place.PlaceId ?? "place"}";

    private static string? TryGetProviderMetadataString(
        Dictionary<string, Dictionary<string, object>>? providerMetadata,
        string key)
    {
        if (providerMetadata is null)
            return null;

        foreach (var scopedMetadata in providerMetadata.Values)
        {
            if (!scopedMetadata.TryGetValue(key, out var value) || value is null)
                continue;

            return value switch
            {
                JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
                JsonElement json => json.ToString(),
                _ => value.ToString()
            };
        }

        return null;
    }

    private static bool? ExtractProviderMetadataBool(
        Dictionary<string, Dictionary<string, object>>? providerMetadata,
        string key)
    {
        if (providerMetadata is null)
            return null;

        foreach (var scopedMetadata in providerMetadata.Values)
        {
            if (!scopedMetadata.TryGetValue(key, out var value) || value is null)
                continue;

            if (value is bool boolValue)
                return boolValue;

            if (value is JsonElement json && json.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return json.GetBoolean();

            if (bool.TryParse(value.ToString(), out var parsed))
                return parsed;
        }

        return null;
    }

    private static bool TryCreateInteractionImageToolResult(
        InteractionStreamImageState? image,
        out CallToolResult result)
    {
        result = default!;

        if (image is null
            || string.IsNullOrWhiteSpace(image.Data)
            || string.IsNullOrWhiteSpace(image.MimeType)
            || !TryDecodeInteractionImageBytes(image.Data, out var bytes))
        {
            return false;
        }

        result = new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(bytes, image.MimeType)
            ]
        };
        return true;
    }

    private static bool TryDecodeInteractionImageBytes(string? data, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrWhiteSpace(data))
            return false;

        try
        {
            bytes = Convert.FromBase64String(data.StripBase64Prefix());
            return bytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ToInteractionImageDataUrl(string? mimeType, string? data)
        => $"data:{(string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType)};base64,{(data ?? string.Empty).StripBase64Prefix()}";

    private static string? GetDeltaAdditionalString(InteractionContentDeltaEvent delta, string key)
    {
        if (delta.Delta?.AdditionalProperties is null
            || !delta.Delta.AdditionalProperties.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static object? GetDeltaAdditionalObject(InteractionContentDeltaEvent delta, string key)
    {
        if (delta.Delta?.AdditionalProperties is null
            || !delta.Delta.AdditionalProperties.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.Clone();
    }

    private static bool? GetDeltaAdditionalBool(InteractionContentDeltaEvent delta, string key)
    {
        if (delta.Delta?.AdditionalProperties is null
            || !delta.Delta.AdditionalProperties.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static object? TryGetStructuredContent(object? output)
    {
        switch (output)
        {
            case CallToolResult callToolResult when callToolResult.StructuredContent is JsonElement structuredContent:
                return structuredContent.Clone();
            case JsonElement json when json.ValueKind == JsonValueKind.Object
                                   && json.TryGetProperty("structuredContent", out var inlineStructuredContent):
                return inlineStructuredContent.Clone();
            default:
                return CloneIfJsonElement(output);
        }
    }

    private static object? TryGetStructuredContentResult(object? output)
    {
        var structuredContent = TryGetStructuredContent(output);
        if (structuredContent is JsonElement json && json.ValueKind == JsonValueKind.Object)
        {
            if (json.TryGetProperty("result", out var result))
                return result.Clone();

            if (json.TryGetProperty("content", out var content))
                return content.Clone();
        }

        return structuredContent;
    }
}
