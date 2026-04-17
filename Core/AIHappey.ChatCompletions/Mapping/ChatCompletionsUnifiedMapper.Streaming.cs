using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.ChatCompletions.Mapping;

public static partial class ChatCompletionsUnifiedMapper
{
    public static AIStreamEvent ToUnifiedStreamEvent(this ChatCompletionUpdate update, string providerId)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var raw = JsonSerializer.SerializeToElement(update, Json);
        return raw.ToUnifiedStreamEvent(providerId);
    }

    public static IEnumerable<AIStreamEvent> ToUnifiedStreamEvents(
        this ChatCompletionUpdate update,
        string providerId,
        ChatCompletionsStreamMappingState? state = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var chunk = JsonSerializer.SerializeToElement(update, Json);
        if (IsHeartbeatChunk(chunk))
            yield break;

        var metadata = BuildUnifiedStreamMetadata(chunk);
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(
            ExtractValue<long?>(chunk, "created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        if (state is not null)
        {
            foreach (var reasoningEvent in MapPerplexityReasoningEvents(chunk, providerId, timestamp, metadata, state))
                yield return reasoningEvent;

            foreach (var toolEvent in MapToolCallEvents(chunk, providerId, timestamp, metadata, state))
                yield return toolEvent;
        }

        var emittedUiEnvelope = false;
        foreach (var mappedEnvelope in MapUiEnvelopes(chunk))
        {
            emittedUiEnvelope = true;
            yield return new AIStreamEvent
            {
                ProviderId = providerId,
                Event = mappedEnvelope,
                Metadata = metadata
            };
        }

        if (!emittedUiEnvelope && !HasToolCallDelta(chunk))
            yield break;

        foreach (var sourceEvent in MapSourceUrlEvents(chunk, providerId, timestamp, metadata, state))
            yield return sourceEvent;
    }

    public static IEnumerable<AIStreamEvent> FinalizeUnifiedStreamEvents(
        string providerId,
        ChatCompletionsStreamMappingState? state,
        DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (state is null || state.PendingToolCalls.Count == 0)
            yield break;

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        foreach (var evt in EmitPendingToolInputs(providerId, ts, [], state))
            yield return evt;
    }

    public static AIStreamEvent ToUnifiedStreamEvent(this JsonElement update, string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (update.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Chat completion stream chunk JSON must be an object.", nameof(update));

        var envelope = TryMapUiEnvelope(update)
            ?? new AIEventEnvelope
            {
                Type = $"data-{ExtractValue<string>(update, "object") ?? "chat.completion.chunk"}",
                Id = ExtractValue<string>(update, "id"),
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(
                    ExtractValue<long?>(update, "created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                Data = new AIDataEventData
                {
                    Id = ExtractValue<string>(update, "id"),
                    Data = update.Clone()
                },
                Output = ParseChunkOutput(update)
            };

        return new AIStreamEvent
        {
            ProviderId = providerId,
            Event = envelope,
            Metadata = BuildUnifiedStreamMetadata(update)
        };
    }

    public static ChatCompletionUpdate ToChatCompletionUpdate(this AIStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        var data = streamEvent.Event.Data;
        if (data is AIDataEventData dataEvent && dataEvent.Data is JsonElement wrappedChunkEl && wrappedChunkEl.ValueKind == JsonValueKind.Object)
        {
            return new ChatCompletionUpdate
            {
                Id = ExtractValue<string>(wrappedChunkEl, "id") ?? $"chatcmpl_{Guid.NewGuid():N}",
                Object = ExtractValue<string>(wrappedChunkEl, "object") ?? "chat.completion.chunk",
                Created = ExtractValue<long?>(wrappedChunkEl, "created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = ExtractValue<string>(wrappedChunkEl, "model") ?? "unknown",
                ServiceTier = ExtractValue<string>(wrappedChunkEl, "service_tier"),
                Choices = ExtractEnumerable(wrappedChunkEl, "choices"),
                Usage = wrappedChunkEl.TryGetProperty("usage", out var wrappedUsageEl) ? wrappedUsageEl.Clone() : null,
                AdditionalProperties = ExtractAdditionalProperties(wrappedChunkEl, KnownChatCompletionStreamFields)
            };
        }

        if (data is JsonElement chunkEl && chunkEl.ValueKind == JsonValueKind.Object)
        {
            return new ChatCompletionUpdate
            {
                Id = ExtractValue<string>(chunkEl, "id") ?? $"chatcmpl_{Guid.NewGuid():N}",
                Object = ExtractValue<string>(chunkEl, "object") ?? "chat.completion.chunk",
                Created = ExtractValue<long?>(chunkEl, "created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = ExtractValue<string>(chunkEl, "model") ?? "unknown",
                ServiceTier = ExtractValue<string>(chunkEl, "service_tier"),
                Choices = ExtractEnumerable(chunkEl, "choices"),
                Usage = chunkEl.TryGetProperty("usage", out var usageEl) ? usageEl.Clone() : null,
                AdditionalProperties = ExtractAdditionalProperties(chunkEl, KnownChatCompletionStreamFields)
            };
        }

        var metadata = streamEvent.Metadata;
        return new ChatCompletionUpdate
        {
            Id = ExtractMetadataValue<string>(metadata, "chatcompletions.stream.id") ?? $"chatcmpl_{Guid.NewGuid():N}",
            Object = ExtractMetadataValue<string>(metadata, "chatcompletions.stream.object") ?? "chat.completion.chunk",
            Created = ExtractMetadataValue<long?>(metadata, "chatcompletions.stream.created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = ExtractMetadataValue<string>(metadata, "chatcompletions.stream.model") ?? "unknown",
            ServiceTier = ExtractMetadataValue<string>(metadata, "chatcompletions.stream.service_tier"),
            Choices = new List<object>(),
            Usage = null,
            AdditionalProperties = BuildChatCompletionUpdateAdditionalProperties(metadata)
        };
    }

    private static Dictionary<string, object?> BuildUnifiedStreamMetadata(JsonElement chunk)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["chatcompletions.stream.raw"] = chunk.Clone()
        };

        foreach (var prop in chunk.EnumerateObject())
            metadata[$"chatcompletions.stream.{prop.Name}"] = prop.Value.Clone();

        if (chunk.TryGetProperty("search_results", out var searchResults))
            metadata["chatcompletions.stream.provider.search_results"] = searchResults.Clone();

        if (chunk.TryGetProperty("citations", out var citations))
            metadata["chatcompletions.stream.provider.citations"] = citations.Clone();

        if (chunk.TryGetProperty("images", out var images))
            metadata["chatcompletions.stream.provider.images"] = images.Clone();

        if (chunk.TryGetProperty("related_questions", out var relatedQuestions))
            metadata["chatcompletions.stream.provider.related_questions"] = relatedQuestions.Clone();

        if (chunk.TryGetProperty("readURLs", out var readUrls))
            metadata["chatcompletions.stream.provider.readURLs"] = readUrls.Clone();

        if (chunk.TryGetProperty("visitedURLs", out var visitedUrls))
            metadata["chatcompletions.stream.provider.visitedURLs"] = visitedUrls.Clone();

        return metadata;
    }

    private static IEnumerable<AIStreamEvent> MapSourceUrlEvents(
        JsonElement chunk,
        string providerId,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata,
        ChatCompletionsStreamMappingState? state)
    {
        if (string.Equals(providerId, "jina", StringComparison.OrdinalIgnoreCase)
            && chunk.TryGetProperty("readURLs", out var readUrls)
            && readUrls.ValueKind == JsonValueKind.Array)
        {
            foreach (var readUrl in readUrls.EnumerateArray())
            {
                if (readUrl.ValueKind != JsonValueKind.String)
                    continue;

                var url = readUrl.GetString();
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                if (state is not null && !state.SeenSourceUrls.Add(url))
                    continue;

                yield return new AIStreamEvent
                {
                    ProviderId = providerId,
                    Event = new AIEventEnvelope
                    {
                        Type = "source-url",
                        Id = ExtractValue<string>(chunk, "id"),
                        Timestamp = timestamp,
                        Data = new AISourceUrlEventData
                        {
                            SourceId = url,
                            Url = url,
                            Title = url,
                            Type = "readURLs"
                        }
                    },
                    Metadata = metadata
                };
            }
        }

        if (!chunk.TryGetProperty("search_results", out var searchResults)
            || searchResults.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var source in searchResults.EnumerateArray())
        {
            var url = ExtractValue<string>(source, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            if (state is not null && !state.SeenSourceUrls.Add(url))
                continue;

            var title = ExtractValue<string>(source, "title") ?? url;
            var providerMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                [providerId] = new Dictionary<string, object>
                {
                    ["date"] = ExtractValue<string>(source, "date") ?? string.Empty,
                    ["lastUpdated"] = ExtractValue<string>(source, "last_updated") ?? string.Empty,
                    ["snippet"] = ExtractValue<string>(source, "snippet") ?? string.Empty,
                    ["source"] = ExtractValue<string>(source, "source") ?? string.Empty
                }
            };

            yield return new AIStreamEvent
            {
                ProviderId = providerId,
                Event = new AIEventEnvelope
                {
                    Type = "source-url",
                    Id = ExtractValue<string>(chunk, "id"),
                    Timestamp = timestamp,
                    Data = new AISourceUrlEventData
                    {
                        SourceId = url,
                        Url = url,
                        Title = title,
                        Type = "url_citation",
                        ProviderMetadata = providerMetadata
                    }
                },
                Metadata = metadata
            };
        }
    }

    private static IEnumerable<AIEventEnvelope> MapUiEnvelopes(JsonElement chunk)
    {
        var errorEnvelope = TryMapTopLevelErrorEnvelope(chunk);
        if (errorEnvelope is not null)
        {
            yield return errorEnvelope;
            yield break;
        }

        if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object)
                continue;

            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("role", out var roleEl)
                    && roleEl.ValueKind == JsonValueKind.String
                    && string.Equals(roleEl.GetString(), "assistant", StringComparison.OrdinalIgnoreCase)
                    && !delta.TryGetProperty("content", out _)
                    && !delta.TryGetProperty("reasoning", out _))
                {
                    yield return CreateUiEnvelope(chunk, "text-start", new AITextStartEventData());
                }

                var deltaType = ExtractValue<string>(delta, "type");
                var isReasoningDelta = string.Equals(deltaType, "think", StringComparison.OrdinalIgnoreCase);

                if (!isReasoningDelta
                    && delta.TryGetProperty("reasoning", out var reasoningEl)
                    && reasoningEl.ValueKind == JsonValueKind.String)
                {
                    isReasoningDelta = true;
                }

                if (isReasoningDelta)
                {
                    var reasoningDelta = ExtractValue<string>(delta, "reasoning");

                    if (string.IsNullOrEmpty(reasoningDelta)
                        && delta.TryGetProperty("content", out var reasoningContentEl))
                    {
                        reasoningDelta = reasoningContentEl.ValueKind == JsonValueKind.String
                            ? reasoningContentEl.GetString()
                            : ChatMessageContentExtensions.ToText(reasoningContentEl);
                    }

                    if (!string.IsNullOrEmpty(reasoningDelta))
                    {
                        yield return CreateUiEnvelope(chunk, "reasoning-delta", new AIReasoningDeltaEventData
                        {
                            Delta = reasoningDelta
                        });
                    }
                }
                else if (delta.TryGetProperty("content", out var contentEl))
                {
                    var textDelta = contentEl.ValueKind == JsonValueKind.String
                        ? contentEl.GetString()
                        : ChatMessageContentExtensions.ToText(contentEl);

                    if (!string.IsNullOrEmpty(textDelta))
                    {
                        yield return CreateUiEnvelope(chunk, "text-delta", new AITextDeltaEventData
                        {
                            Delta = textDelta
                        });
                    }
                }

                if (delta.TryGetProperty("tool_calls", out _))
                    continue;
            }

            var finishReason = ExtractValue<string>(choice, "finish_reason");
            if (!string.IsNullOrWhiteSpace(finishReason))
            {
                if (string.Equals(finishReason, "thinking_end", StringComparison.OrdinalIgnoreCase))
                    continue;

                var usage = chunk.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object
                    ? usageEl
                    : default;

                int? inputTokens = null;
                int? outputTokens = null;
                int? totalTokens = null;

                if (usage.ValueKind == JsonValueKind.Object)
                {
                    inputTokens = ExtractValue<int?>(usage, "prompt_tokens");
                    outputTokens = ExtractValue<int?>(usage, "completion_tokens");
                    totalTokens = ExtractValue<int?>(usage, "total_tokens");
                }

                var finishReasonValue = finishReason == "tool_calls"
                        || finishReason == "function_call" ? "tool-calls" :
                        finishReason == "content_filter" ? "content-filter" : finishReason;

                yield return CreateUiEnvelope(chunk, "finish", new AIFinishEventData
                {
                    FinishReason = finishReasonValue,
                    Model = ExtractValue<string>(chunk, "model"),
                    CompletedAt = ExtractValue<long?>(chunk, "created"),
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = totalTokens
                });
            }
        }
    }

    private static AIEventEnvelope? TryMapUiEnvelope(JsonElement chunk)
        => MapUiEnvelopes(chunk).FirstOrDefault();

    private static AIEventEnvelope CreateUiEnvelope(JsonElement chunk, string type, object data)
    {
        return new AIEventEnvelope
        {
            Type = type,
            Id = ExtractValue<string>(chunk, "id"),
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(
                ExtractValue<long?>(chunk, "created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Data = data,
            Output = ParseChunkOutput(chunk),
            Metadata = []
        };
    }

    private static AIEventEnvelope? TryMapTopLevelErrorEnvelope(JsonElement chunk)
    {
        if (!chunk.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return null;

        var errorText = BuildTopLevelErrorText(error);
        if (string.IsNullOrWhiteSpace(errorText))
            errorText = error.GetRawText();

        return CreateUiEnvelope(chunk, "error", new AIErrorEventData
        {
            ErrorText = errorText
        });
    }

    private static string BuildTopLevelErrorText(JsonElement error)
    {
        var lines = new List<string>();

        var message = ExtractValue<string>(error, "message");
        var type = ExtractValue<string>(error, "type");
        var code = ExtractValue<string>(error, "code");
        var failedGeneration = ExtractValue<string>(error, "failed_generation");
        var statusCode = ExtractValue<int?>(error, "status_code");

        if (!string.IsNullOrWhiteSpace(message))
            lines.Add(message);

        if (!string.IsNullOrWhiteSpace(type))
            lines.Add($"type: {type}");

        if (!string.IsNullOrWhiteSpace(code))
            lines.Add($"code: {code}");

        if (statusCode is not null)
            lines.Add($"status_code: {statusCode}");

        if (!string.IsNullOrWhiteSpace(failedGeneration))
            lines.Add($"failed_generation: {failedGeneration}");

        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsHeartbeatChunk(JsonElement chunk)
    {
        if (chunk.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            return false;

        var id = ExtractValue<string>(chunk, "id");
        var model = ExtractValue<string>(chunk, "model");
        var created = ExtractValue<long?>(chunk, "created") ?? 0;
        var hasUsage = chunk.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null;

        var hasChoices = chunk.TryGetProperty("choices", out var choices)
                         && choices.ValueKind == JsonValueKind.Array
                         && choices.EnumerateArray().Any();

        return string.IsNullOrWhiteSpace(id)
               && string.IsNullOrWhiteSpace(model)
               && created <= 0
               && !hasUsage
               && !hasChoices;
    }

    private static bool HasToolCallDelta(JsonElement chunk)
    {
        if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object)
                continue;

            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                continue;

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                return true;
        }

        return false;
    }

    private static IEnumerable<AIStreamEvent> MapPerplexityReasoningEvents(
        JsonElement chunk,
        string providerId,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata,
        ChatCompletionsStreamMappingState state)
    {
        var chunkObject = ExtractValue<string>(chunk, "object");
        if (!string.Equals(chunkObject, "chat.reasoning", StringComparison.OrdinalIgnoreCase))
            yield break;

        if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            yield break;

        var chunkId = ExtractValue<string>(chunk, "id") ?? $"chat_reasoning_{Guid.NewGuid():N}";

        var choiceIndex = 0;
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object
                || !TryGetPerplexityReasoningSteps(choice, out var reasoningSteps)
                || reasoningSteps.ValueKind != JsonValueKind.Array)
            {
                choiceIndex++;
                continue;
            }

            var stepIndex = 0;
            foreach (var step in reasoningSteps.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object)
                {
                    stepIndex++;
                    continue;
                }

                var stateKey = $"{chunkId}:{choiceIndex}:{stepIndex}:{ExtractValue<string>(step, "type") ?? "reasoning"}";
                if (!state.PerplexityReasoningSteps.TryGetValue(stateKey, out var acc))
                {
                    acc = new PerplexityReasoningAccumulator
                    {
                        ReasoningId = $"{chunkId}:reasoning:{choiceIndex}:{stepIndex}",
                        ToolCallId = $"{chunkId}:tool:{choiceIndex}:{stepIndex}",
                        ToolName = ExtractValue<string>(step, "type") ?? "web_search"
                    };

                    state.PerplexityReasoningSteps[stateKey] = acc;
                }

                var providerMetadata = BuildPerplexityReasoningMetadata(step);
                var reasoningProviderMetadata = CreateReasoningProviderMetadata(providerId, providerMetadata);
                var thought = ExtractValue<string>(step, "thought");

                if (!acc.ReasoningStarted)
                {
                    acc.ReasoningStarted = true;
                    yield return new AIStreamEvent
                    {
                        ProviderId = providerId,
                        Event = new AIEventEnvelope
                        {
                            Type = "reasoning-start",
                            Id = acc.ReasoningId,
                            Timestamp = timestamp,
                            Data = new AIReasoningStartEventData
                            {
                                ProviderMetadata = reasoningProviderMetadata
                            }
                        },
                        Metadata = metadata
                    };
                }

                if (!string.IsNullOrWhiteSpace(thought) && !acc.ReasoningDeltaEmitted)
                {
                    acc.ReasoningDeltaEmitted = true;
                    yield return new AIStreamEvent
                    {
                        ProviderId = providerId,
                        Event = new AIEventEnvelope
                        {
                            Type = "reasoning-delta",
                            Id = acc.ReasoningId,
                            Timestamp = timestamp,
                            Data = new AIReasoningDeltaEventData
                            {
                                Delta = thought,
                                ProviderMetadata = reasoningProviderMetadata
                            }
                        },
                        Metadata = metadata
                    };
                }

                if (TryGetPerplexityWebSearch(step, out var webSearch))
                {
                    if (!acc.ToolInputStarted)
                    {
                        acc.ToolInputStarted = true;
                        yield return new AIStreamEvent
                        {
                            ProviderId = providerId,
                            Event = new AIEventEnvelope
                            {
                                Type = "tool-input-start",
                                Id = acc.ToolCallId,
                                Timestamp = timestamp,
                                Data = new AIToolInputStartEventData
                                {
                                    ToolName = acc.ToolName,
                                    ProviderExecuted = true,
                                    Title = "Web search"
                                }
                            },
                            Metadata = metadata
                        };
                    }

                    if (!acc.ToolInputAvailableEmitted
                        && webSearch.TryGetProperty("search_keywords", out var searchKeywords)
                        && searchKeywords.ValueKind == JsonValueKind.Array)
                    {
                        acc.ToolInputAvailableEmitted = true;
                        yield return new AIStreamEvent
                        {
                            ProviderId = providerId,
                            Event = new AIEventEnvelope
                            {
                                Type = "tool-input-available",
                                Id = acc.ToolCallId,
                                Timestamp = timestamp,
                                Data = new AIToolInputAvailableEventData
                                {
                                    ToolName = acc.ToolName,
                                    ProviderExecuted = true,
                                    Title = "Web search",
                                    Input = CreatePerplexityToolInput(searchKeywords)
                                }
                            },
                            Metadata = metadata
                        };
                    }

                    if (!acc.ToolOutputAvailableEmitted
                        && webSearch.TryGetProperty("search_results", out var searchResults)
                        && searchResults.ValueKind == JsonValueKind.Array)
                    {
                        acc.ToolOutputAvailableEmitted = true;
                        yield return new AIStreamEvent
                        {
                            ProviderId = providerId,
                            Event = new AIEventEnvelope
                            {
                                Type = "tool-output-available",
                                Id = acc.ToolCallId,
                                Timestamp = timestamp,
                                Data = new AIToolOutputAvailableEventData
                                {
                                    ProviderExecuted = true,
                                    Output = CreatePerplexityToolOutput(searchResults)
                                }
                            },
                            Metadata = metadata
                        };
                    }
                }

                if (!acc.ReasoningEnded)
                {
                    acc.ReasoningEnded = true;
                    yield return new AIStreamEvent
                    {
                        ProviderId = providerId,
                        Event = new AIEventEnvelope
                        {
                            Type = "reasoning-end",
                            Id = acc.ReasoningId,
                            Timestamp = timestamp,
                            Data = new AIReasoningEndEventData
                            {
                                ProviderMetadata = reasoningProviderMetadata
                            }
                        },
                        Metadata = metadata
                    };
                }

                stepIndex++;
            }

            choiceIndex++;
        }
    }

    private static IEnumerable<AIStreamEvent> MapToolCallEvents(
        JsonElement chunk,
        string providerId,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata,
        ChatCompletionsStreamMappingState state)
    {
        if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object)
                continue;

            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("tool_calls", out var toolCalls)
                && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    if (toolCall.ValueKind != JsonValueKind.Object)
                        continue;

                    var index = ExtractValue<int?>(toolCall, "index") ?? 0;
                    if (!state.PendingToolCalls.TryGetValue(index, out var acc))
                    {
                        acc = new ToolCallAccumulator();
                        state.PendingToolCalls[index] = acc;
                    }

                    acc.Id ??= ExtractValue<string>(toolCall, "id") ?? $"call_{Guid.NewGuid():N}";
                    acc.Type ??= ExtractValue<string>(toolCall, "type") ?? "function";

                    if (toolCall.TryGetProperty("function", out var functionEl) && functionEl.ValueKind == JsonValueKind.Object)
                    {
                        acc.Name ??= ExtractValue<string>(functionEl, "name") ?? "unknown_tool";
                        var argsDelta = ExtractValue<string>(functionEl, "arguments");

                        if (!acc.Started)
                        {
                            acc.Started = true;
                            yield return CreateToolInputStartEvent(providerId, acc, timestamp, metadata);
                        }

                        if (!string.IsNullOrEmpty(argsDelta))
                        {
                            acc.Arguments += argsDelta;
                            yield return CreateToolInputDeltaEvent(providerId, acc, argsDelta, timestamp, metadata);
                        }
                    }
                    else if (toolCall.TryGetProperty("custom", out var customEl) && customEl.ValueKind == JsonValueKind.Object)
                    {
                        acc.Name ??= ExtractValue<string>(customEl, "name") ?? "custom_tool";
                        var inputDelta = ExtractValue<string>(customEl, "input");

                        if (!acc.Started)
                        {
                            acc.Started = true;
                            yield return CreateToolInputStartEvent(providerId, acc, timestamp, metadata);
                        }

                        if (!string.IsNullOrEmpty(inputDelta))
                        {
                            acc.Arguments += inputDelta;
                            yield return CreateToolInputDeltaEvent(providerId, acc, inputDelta, timestamp, metadata);
                        }
                    }
                }
            }

            var finishReason = ExtractValue<string>(choice, "finish_reason");
            if (string.Equals(finishReason, "tool_calls", StringComparison.OrdinalIgnoreCase)
                || string.Equals(finishReason, "stop", StringComparison.OrdinalIgnoreCase)
                || string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)
                || string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var completed in EmitPendingToolInputs(providerId, timestamp, metadata, state))
                    yield return completed;
            }
        }
    }

    private static IEnumerable<AIStreamEvent> EmitPendingToolInputs(
        string providerId,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata,
        ChatCompletionsStreamMappingState state)
    {
        foreach (var kvp in state.PendingToolCalls.OrderBy(a => a.Key))
        {
            var acc = kvp.Value;
            if (acc.EmittedAvailable)
                continue;

            acc.EmittedAvailable = true;

            yield return new AIStreamEvent
            {
                ProviderId = providerId,
                Event = new AIEventEnvelope
                {
                    Type = "tool-input-available",
                    Id = acc.Id,
                    Timestamp = timestamp,
                    Data = new AIToolInputAvailableEventData
                    {
                        ProviderExecuted = false,
                        ToolName = acc.Name ?? "unknown_tool",
                        Input = ParseToolInput(acc.Arguments)
                    }
                },
                Metadata = metadata
            };
        }

        state.PendingToolCalls.Clear();
    }

    private static AIStreamEvent CreateToolInputStartEvent(
        string providerId,
        ToolCallAccumulator acc,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "tool-input-start",
                Id = acc.Id,
                Timestamp = timestamp,
                Data = new AIToolInputStartEventData
                {
                    ProviderExecuted = false,
                    ToolName = acc.Name ?? "unknown_tool"
                }
            },
            Metadata = metadata
        };

    private static AIStreamEvent CreateToolInputDeltaEvent(
        string providerId,
        ToolCallAccumulator acc,
        string delta,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "tool-input-delta",
                Id = acc.Id,
                Timestamp = timestamp,
                Data = new AIToolInputDeltaEventData
                {
                    InputTextDelta = delta
                }
            },
            Metadata = metadata
        };

    private static object ParseToolInput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new { };

        try
        {
            return JsonSerializer.Deserialize<object>(raw, Json) ?? new { };
        }
        catch
        {
            return new Dictionary<string, object?> { ["raw"] = raw };
        }
    }

    private static bool TryGetPerplexityReasoningSteps(JsonElement choice, out JsonElement reasoningSteps)
    {
        reasoningSteps = default;

        if (choice.TryGetProperty("delta", out var delta)
            && delta.ValueKind == JsonValueKind.Object
            && delta.TryGetProperty("reasoning_steps", out reasoningSteps)
            && reasoningSteps.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (choice.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("reasoning_steps", out reasoningSteps)
            && reasoningSteps.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        reasoningSteps = default;
        return false;
    }

    private static bool TryGetPerplexityWebSearch(JsonElement step, out JsonElement webSearch)
    {
        webSearch = default;

        return step.TryGetProperty("web_search", out webSearch)
               && webSearch.ValueKind == JsonValueKind.Object;
    }

    private static Dictionary<string, object> BuildPerplexityReasoningMetadata(JsonElement step)
    {
        var metadata = new Dictionary<string, object>();

        if (step.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
            metadata["type"] = typeEl.GetString() ?? string.Empty;

        metadata["step"] = step.Clone();
        return metadata;
    }

    private static Dictionary<string, Dictionary<string, object>>? CreateReasoningProviderMetadata(
        string providerId,
        Dictionary<string, object>? providerMetadata)
        => providerMetadata is null || providerMetadata.Count == 0
            ? null
            : new Dictionary<string, Dictionary<string, object>>
            {
                [providerId] = providerMetadata
            };

    private static JsonElement CreatePerplexityToolInput(JsonElement searchKeywords)
        => JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonElement>
            {
                ["search_keywords"] = searchKeywords.Clone()
            },
            JsonSerializerOptions.Web);

    private static JsonElement CreatePerplexityToolOutput(JsonElement searchResults)
        => JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonElement>
            {
                ["search_results"] = searchResults.Clone()
            },
            JsonSerializerOptions.Web);

    internal sealed class ToolCallAccumulator
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
        public bool Started { get; set; }
        public bool EmittedAvailable { get; set; }
        public string Arguments { get; set; } = string.Empty;
    }

    internal sealed class PerplexityReasoningAccumulator
    {
        public required string ReasoningId { get; init; }
        public required string ToolCallId { get; init; }
        public required string ToolName { get; init; }
        public bool ReasoningStarted { get; set; }
        public bool ReasoningDeltaEmitted { get; set; }
        public bool ReasoningEnded { get; set; }
        public bool ToolInputStarted { get; set; }
        public bool ToolInputAvailableEmitted { get; set; }
        public bool ToolOutputAvailableEmitted { get; set; }
    }

    public sealed class ChatCompletionsStreamMappingState
    {
        internal Dictionary<int, ToolCallAccumulator> PendingToolCalls { get; } = [];

        internal Dictionary<string, PerplexityReasoningAccumulator> PerplexityReasoningSteps { get; } = [];

        internal HashSet<string> SeenSourceUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
