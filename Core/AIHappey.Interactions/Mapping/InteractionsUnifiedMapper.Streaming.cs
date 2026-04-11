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
                                    Title = unifiedContent.Title
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
                                    Output = CloneIfJsonElement(unifiedContent.Output) ?? new { },
                                    ProviderExecuted = unifiedContent.ProviderExecuted
                                }
                            },
                            part,
                            start.Index);
                    }

                    if (emittedToolEvent)
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

            case InteractionContentDeltaEvent delta when string.Equals(delta.Delta?.Type, "google_search_call", StringComparison.OrdinalIgnoreCase):
            {
                var toolCallId = GetDeltaAdditionalString(delta, "id")
                                 ?? GetDeltaAdditionalString(delta, "call_id")
                                 ?? BuildContentEventId(delta.Index);
                var toolName = "google_search";
                var input = GetDeltaAdditionalObject(delta, "arguments") ?? JsonSerializer.SerializeToElement(new { }, Json);
                var providerMetadata = CreateGoogleSearchToolProviderMetadata(
                    providerId,
                    toolCallId,
                    "google_search_call",
                    searchType: GetDeltaAdditionalString(delta, "search_type"));

                yield return CreateStreamEvent(
                    providerId,
                    new AIEventEnvelope
                    {
                        Type = "tool-input-start",
                        Id = toolCallId,
                        Data = new AIToolInputStartEventData
                        {
                            ToolName = toolName,
                            ProviderExecuted = true,
                            Title = toolName
                        }
                    },
                    part,
                    delta.Index);

                var inputJson = SerializePayload(input, "{}");
                yield return CreateStreamEvent(
                    providerId,
                    new AIEventEnvelope
                    {
                        Type = "tool-input-delta",
                        Id = toolCallId,
                        Data = new AIToolInputDeltaEventData
                        {
                            InputTextDelta = inputJson
                        }
                    },
                    part,
                    delta.Index);

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

                yield return CreateStreamEvent(providerId, CreateDataEnvelope(part, delta.Index), part, delta.Index);
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
                                Content = [new TextContentBlock { Text = SerializePayload(resultPayload, "[]") }],
                                StructuredContent = structuredContent.Clone()
                            },
                            ProviderExecuted = true,
                            ProviderMetadata = providerMetadata
                        }
                    },
                    part,
                    delta.Index);

                yield return CreateStreamEvent(providerId, CreateDataEnvelope(part, delta.Index), part, delta.Index);
                yield break;
            }

            case InteractionContentDeltaEvent delta when HasUrlCitationAnnotations(delta):
            {
                var annotations = GetUrlCitationAnnotations(delta);
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
                                Title = annotation.Title,
                                Type = annotation.Type,
                                ProviderMetadata = CreateProviderScopedMetadata(providerId, new Dictionary<string, object>
                                {
                                    ["type"] = annotation.Type ?? "url_citation",
                                    ["start_index"] = annotation.StartIndex ?? -1,
                                    ["end_index"] = annotation.EndIndex ?? -1,
                                    ["interactions.content.index"] = delta.Index,
                                    ["interactions.event_id"] = delta.EventId ?? string.Empty
                                })
                            }
                        },
                        part,
                        delta.Index);
                }

                yield return CreateStreamEvent(providerId, CreateDataEnvelope(part, delta.Index), part, delta.Index);
                yield break;
            }

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
                if (complete.Interaction is not null)
                {
                    yield return CreateStreamEvent(
                        providerId,
                        CreateResponseDataEnvelope(complete.Interaction),
                        part,
                        0);
                }

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

    private static InteractionContent CreateInteractionToolContentFromInput(object? data)
    {
        var payload = DeserializeFromObject<AIToolInputAvailableEventData>(data);
        var input = CloneIfJsonElement(payload?.Input);

        if (string.Equals(TryGetProviderMetadataString(payload?.ProviderMetadata, "type"), "google_search_call", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractionGoogleSearchCallContent
            {
                Id = TryGetProviderMetadataString(payload?.ProviderMetadata, "tool_use_id") ?? Guid.NewGuid().ToString("N"),
                SearchType = TryGetProviderMetadataString(payload?.ProviderMetadata, "search_type"),
                Arguments = DeserializeFromObject<InteractionGoogleSearchCallArguments>(input)
            };
        }

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

        if (string.Equals(TryGetProviderMetadataString(payload?.ProviderMetadata, "type"), "google_search_result", StringComparison.OrdinalIgnoreCase))
        {
            return new InteractionGoogleSearchResultContent
            {
                CallId = TryGetProviderMetadataString(payload?.ProviderMetadata, "tool_use_id") ?? Guid.NewGuid().ToString("N"),
                IsError = ExtractProviderMetadataBool(payload?.ProviderMetadata, "interactions.is_error"),
                Result = DeserializeFromObject<List<InteractionGoogleSearchResult>>(TryGetStructuredContentResult(payload?.Output))
            };
        }

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

    private static Dictionary<string, Dictionary<string, object>> CreateGoogleSearchToolProviderMetadata(
        string providerId,
        string toolCallId,
        string type,
        string? searchType = null,
        bool? isError = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["type"] = type,
            ["tool_use_id"] = toolCallId,
            ["tool_name"] = "google_search",
            ["name"] = "google_search",
            ["title"] = "google_search"
        };

        if (!string.IsNullOrWhiteSpace(searchType))
            metadata["search_type"] = searchType;

        if (isError is not null)
            metadata["interactions.is_error"] = isError.Value;

        return new Dictionary<string, Dictionary<string, object>>
        {
            [providerId] = metadata
        };
    }

    private static bool HasUrlCitationAnnotations(InteractionContentDeltaEvent delta)
        => GetUrlCitationAnnotations(delta).Count != 0;

    private static List<InteractionAnnotation> GetUrlCitationAnnotations(InteractionContentDeltaEvent delta)
    {
        if (delta.Delta?.AdditionalProperties is null
            || !delta.Delta.AdditionalProperties.TryGetValue("annotations", out var annotations)
            || annotations.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<InteractionAnnotation>>(annotations.GetRawText(), Json) ?? [])
                .Where(annotation => string.Equals(annotation.Type, "url_citation", StringComparison.OrdinalIgnoreCase)
                                     && !string.IsNullOrWhiteSpace(annotation.Url))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string BuildCitationSourceId(InteractionContentDeltaEvent delta, int ordinal, InteractionAnnotation annotation)
        => $"{delta.EventId ?? BuildContentEventId(delta.Index)}:{delta.Index}:{ordinal}:{annotation.StartIndex ?? -1}:{annotation.EndIndex ?? -1}";

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
