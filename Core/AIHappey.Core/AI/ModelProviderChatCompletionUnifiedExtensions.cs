using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Unified.Models;
using AIHappey.Responses.Mapping;
using AIHappey.ChatCompletions.Mapping;
using static AIHappey.ChatCompletions.Mapping.ChatCompletionsUnifiedMapper;

namespace AIHappey.Core.AI;

public static class ModelProviderChatCompletionUnifiedExtensions
{
    public static async Task<AIResponse> ExecuteUnifiedViaChatCompletionsAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var responseRequest = request.ToChatCompletionOptions(modelProvider.GetIdentifier());
        responseRequest.Stream = false;
        responseRequest.Store ??= false;

        var response = await modelProvider.CompleteChatAsync(responseRequest, cancellationToken);
        return response.ToUnifiedResponse(modelProvider.GetIdentifier());

    }

    public static async IAsyncEnumerable<AIStreamEvent> StreamUnifiedViaChatCompletionsAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        Func<AIRequest, CancellationToken, IAsyncEnumerable<AIStreamEvent>>? fallback = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var responseRequest = request.ToChatCompletionOptions(modelProvider.GetIdentifier());
        responseRequest.Stream = true;
        responseRequest.Store ??= false;

        IAsyncEnumerable<AIStreamEvent> stream = StreamCore();

        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return streamEvent;

        async IAsyncEnumerable<AIStreamEvent> StreamCore()
        {
            var textStarted = false;
            var reasoningStarted = false;
            var finishEmitted = false;
            string? activeId = null;
            string? activeModel = null;
            DateTimeOffset? lastTimestamp = null;
            Dictionary<string, object?>? lastMetadata = null;
            string? lastFinishReason = null;
            int? inputTokens = null;
            int? outputTokens = null;
            int? totalTokens = null;
            var mappingState = new ChatCompletionsUnifiedMapper.ChatCompletionsStreamMappingState();

            await foreach (var update in modelProvider.CompleteChatStreamingAsync(responseRequest, cancellationToken))
            {
                CaptureStreamTail(update, ref activeId, ref activeModel, ref lastTimestamp, ref lastFinishReason, ref inputTokens, ref outputTokens, ref totalTokens);

                foreach (var mapped in update.ToUnifiedStreamEvents(modelProvider.GetIdentifier(), mappingState))
                {
                    var normalizedType = NormalizeEventType(mapped.Event.Type);
                    var eventId = mapped.Event.Id ?? update.Id;

                    if (!string.IsNullOrWhiteSpace(eventId))
                        activeId = eventId;

                    if (!string.IsNullOrWhiteSpace(update.Model))
                        activeModel = update.Model;

                    if (mapped.Event.Timestamp is not null
                        && (update.Created > 0 || normalizedType != "error"))
                    {
                        lastTimestamp = mapped.Event.Timestamp;
                    }

                    lastMetadata = mapped.Metadata ?? lastMetadata;

                    if (normalizedType == "reasoning-start")
                        reasoningStarted = true;

                    if (normalizedType == "reasoning-end")
                        reasoningStarted = false;

                    if (normalizedType == "text-start")
                        textStarted = true;

                    if (normalizedType == "text-end")
                        textStarted = false;

                    if (normalizedType == "reasoning-delta" && !reasoningStarted)
                    {
                        if (textStarted)
                        {
                            textStarted = false;
                            yield return CreateSyntheticStreamEvent(
                                providerId: modelProvider.GetIdentifier(),
                                type: "text-end",
                                id: activeId,
                                timestamp: mapped.Event.Timestamp,
                                metadata: mapped.Metadata);
                        }

                        reasoningStarted = true;
                        yield return CreateSyntheticStreamEvent(
                            providerId: modelProvider.GetIdentifier(),
                            type: "reasoning-start",
                            id: activeId,
                            timestamp: mapped.Event.Timestamp,
                            metadata: mapped.Metadata);
                    }

                    if (normalizedType == "text-delta" && !textStarted)
                    {
                        if (reasoningStarted)
                        {
                            reasoningStarted = false;
                            yield return CreateSyntheticStreamEvent(
                                providerId: modelProvider.GetIdentifier(),
                                type: "reasoning-end",
                                id: activeId,
                                timestamp: mapped.Event.Timestamp,
                                metadata: mapped.Metadata);
                        }

                        textStarted = true;
                        yield return CreateSyntheticStreamEvent(
                            providerId: modelProvider.GetIdentifier(),
                            type: "text-start",
                            id: activeId,
                            timestamp: mapped.Event.Timestamp,
                            metadata: mapped.Metadata);
                    }

                    if (normalizedType == "finish")
                    {
                        finishEmitted = true;

                        if (reasoningStarted)
                        {
                            reasoningStarted = false;
                            yield return CreateSyntheticStreamEvent(
                                providerId: modelProvider.GetIdentifier(),
                                type: "reasoning-end",
                                id: activeId,
                                timestamp: mapped.Event.Timestamp,
                                metadata: mapped.Metadata);
                        }

                        if (textStarted)
                        {
                            textStarted = false;
                            yield return CreateSyntheticStreamEvent(
                                providerId: modelProvider.GetIdentifier(),
                                type: "text-end",
                                id: activeId,
                                timestamp: mapped.Event.Timestamp,
                                metadata: mapped.Metadata);
                        }
                    }

                    yield return mapped;
                }
            }

            foreach (var tail in ChatCompletionsUnifiedMapper.FinalizeUnifiedStreamEvents(
                         modelProvider.GetIdentifier(),
                         mappingState,
                         DateTimeOffset.UtcNow))
            {
                yield return tail;
            }

            if (!finishEmitted && (activeId is not null || activeModel is not null || lastTimestamp is not null))
            {
                var finishTimestamp = lastTimestamp ?? DateTimeOffset.UtcNow;

                if (reasoningStarted)
                {
                    reasoningStarted = false;
                    yield return CreateSyntheticStreamEvent(
                        providerId: modelProvider.GetIdentifier(),
                        type: "reasoning-end",
                        id: activeId,
                        timestamp: finishTimestamp,
                        metadata: lastMetadata);
                }

                if (textStarted)
                {
                    textStarted = false;
                    yield return CreateSyntheticStreamEvent(
                        providerId: modelProvider.GetIdentifier(),
                        type: "text-end",
                        id: activeId,
                        timestamp: finishTimestamp,
                        metadata: lastMetadata);
                }

                yield return CreateSyntheticFinishStreamEvent(
                    providerId: modelProvider.GetIdentifier(),
                    id: activeId,
                    timestamp: finishTimestamp,
                    metadata: lastMetadata,
                    finishReason: lastFinishReason ?? "stop",
                    model: activeModel ?? responseRequest.Model,
                    inputTokens: inputTokens,
                    outputTokens: outputTokens,
                    totalTokens: totalTokens);
            }
        }
    }

    private static string NormalizeEventType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return string.Empty;

        return type.StartsWith("vercel.ui.", StringComparison.OrdinalIgnoreCase)
            ? type["vercel.ui.".Length..]
            : type;
    }

    private static AIStreamEvent CreateSyntheticStreamEvent(
        string providerId,
        string type,
        string? id,
        DateTimeOffset? timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = timestamp,
                Data = type switch
                {
                    "text-start" => new AITextStartEventData(),
                    "text-end" => new AITextEndEventData(),
                    "reasoning-start" => new AIReasoningStartEventData(),
                    "reasoning-end" => new AIReasoningEndEventData(),
                    _ => new AIDataEventData { Data = string.Empty }
                }
            },
            Metadata = metadata
        };

    private static AIStreamEvent CreateSyntheticFinishStreamEvent(
        string providerId,
        string? id,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata,
        string finishReason,
        string? model,
        int? inputTokens,
        int? outputTokens,
        int? totalTokens)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = id,
                Timestamp = timestamp,
                Data = new AIFinishEventData
                {
                    FinishReason = finishReason,
                    Model = model,
                    CompletedAt = timestamp.ToUnixTimeSeconds(),
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = totalTokens ?? ((inputTokens ?? 0) + (outputTokens ?? 0))
                }
            },
            Metadata = metadata
        };

    private static void CaptureStreamTail(
        AIHappey.ChatCompletions.Models.ChatCompletionUpdate update,
        ref string? activeId,
        ref string? activeModel,
        ref DateTimeOffset? lastTimestamp,
        ref string? lastFinishReason,
        ref int? inputTokens,
        ref int? outputTokens,
        ref int? totalTokens)
    {
        if (!string.IsNullOrWhiteSpace(update.Id))
            activeId = update.Id;

        if (!string.IsNullOrWhiteSpace(update.Model))
            activeModel = update.Model;

        if (update.Created > 0)
            lastTimestamp = DateTimeOffset.FromUnixTimeSeconds(update.Created);

        var chunk = JsonSerializer.SerializeToElement(update, JsonSerializerOptions.Web);

        if (chunk.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            lastFinishReason = "error";
            return;
        }

        if (chunk.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var promptTokens) && promptTokens.TryGetInt32(out var parsedPromptTokens))
                inputTokens = parsedPromptTokens;

            if (usage.TryGetProperty("completion_tokens", out var completionTokens) && completionTokens.TryGetInt32(out var parsedCompletionTokens))
                outputTokens = parsedCompletionTokens;

            if (usage.TryGetProperty("total_tokens", out var totalTokensEl) && totalTokensEl.TryGetInt32(out var parsedTotalTokens))
                totalTokens = parsedTotalTokens;
        }

        if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object)
                continue;

            if (!choice.TryGetProperty("finish_reason", out var finishReasonEl) || finishReasonEl.ValueKind != JsonValueKind.String)
                continue;

            var finishReason = finishReasonEl.GetString();
            if (string.IsNullOrWhiteSpace(finishReason))
                continue;

            lastFinishReason = finishReason switch
            {
                "tool_calls" or "function_call" => "tool-calls",
                "content_filter" => "content-filter",
                _ => finishReason
            };
        }
    }

    private static AIStreamEvent CreateDataStreamEvent(
        string providerId,
        string type,
        object payload,
        Dictionary<string, string>? headers = null)
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
                    ["unified.request.headers"] = headers.ToDictionary(a => a.Key, a => (object?)a.Value)
                }
        };

}

