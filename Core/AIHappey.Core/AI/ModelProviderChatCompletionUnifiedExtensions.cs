using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Unified.Models;
using AIHappey.Responses.Mapping;
using AIHappey.ChatCompletions.Mapping;
using static AIHappey.ChatCompletions.Mapping.ChatCompletionsUnifiedMapper;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderChatCompletionUnifiedExtensions
{
    public static async Task<ChatCompletion> GetChatCompletion(
         this IModelProvider modelProvider,
         HttpClient client,
         ChatCompletionOptions options,
         string relativeUrl = "v1/chat/completions",
         JsonElement? extraRootProperties = null,
         Abstractions.Http.ProviderBackendCaptureRequest? capture = null,
         CancellationToken cancellationToken = default)
    {
        var headers = MergeRequestHeaders(modelProvider.SetDefaultChatCompletionProperties(options), options.Headers);

        return await client.GetChatCompletion(options,
            modelProvider.GetIdentifier(),
            relativeUrl,
            extraRootProperties: extraRootProperties,
            capture: capture,
            headers: headers,
            ct: cancellationToken);
    }

    public static async IAsyncEnumerable<ChatCompletionUpdate> GetChatCompletions(
        this IModelProvider modelProvider,
        HttpClient client,
        ChatCompletionOptions options,
        string relativeUrl = "v1/chat/completions",
        JsonElement? extraRootProperties = null,
        Abstractions.Http.ProviderBackendCaptureRequest? capture = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var headers = MergeRequestHeaders(modelProvider.SetDefaultChatCompletionProperties(options), options.Headers);

        await foreach (var update in client.GetChatCompletionUpdates(options,
            relativeUrl: relativeUrl,
            providerId: modelProvider.GetIdentifier(),
            extraRootProperties: extraRootProperties,
            capture: capture,
            headers: headers,
            ct: cancellationToken))
            yield return update;
    }

    private static IReadOnlyDictionary<string, string>? MergeRequestHeaders(
        IReadOnlyDictionary<string, string>? first,
        IReadOnlyDictionary<string, string>? second)
    {
        if ((first is null || first.Count == 0) && (second is null || second.Count == 0))
            return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (first is not null)
        {
            foreach (var (key, value) in first)
                result[key] = value;
        }

        if (second is not null)
        {
            foreach (var (key, value) in second)
            {
                result.Remove(key);
                result[key] = value;
            }
        }

        return result.Count == 0 ? null : result;
    }

    public static async Task<AIResponse> ExecuteUnifiedViaChatCompletionsAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        CancellationToken cancellationToken = default,
        bool enforceFlatContent = false)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var responseRequest = request.ToChatCompletionOptions(modelProvider.GetIdentifier(), enforceFlatContent);
        responseRequest.Stream = false;
        responseRequest.Store ??= false;

        var response = await modelProvider.CompleteChatAsync(responseRequest, cancellationToken);
        return response.ToUnifiedResponse(modelProvider.GetIdentifier());

    }

    public static async IAsyncEnumerable<AIStreamEvent> StreamUnifiedViaChatCompletionsAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        Func<AIRequest, CancellationToken, IAsyncEnumerable<AIStreamEvent>>? fallback = null,
        Func<ChatCompletionUpdate, IEnumerable<AIStreamEvent>>? rawChunkMapper = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        bool enforceFlatContent = false)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var responseRequest = request.ToChatCompletionOptions(modelProvider.GetIdentifier(), enforceFlatContent);
        responseRequest.Stream = true;
        responseRequest.Store ??= false;

        IAsyncEnumerable<AIStreamEvent> stream = StreamCore();

        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return streamEvent;

        async IAsyncEnumerable<AIStreamEvent> StreamCore()
        {
            var textStarted = false;
            var reasoningStarted = false;
            var finishObserved = false;
            string? activeId = null;
            string? activeTextSpanId = null;
            string? activeReasoningSpanId = null;
            string? activeModel = null;
            DateTimeOffset? lastTimestamp = null;
            Dictionary<string, object?>? lastMetadata = null;
            string? lastFinishReason = null;
            int? inputTokens = null;
            int? outputTokens = null;
            int? totalTokens = null;
            object? rawUsage = null;
            AIStreamEvent? pendingFinish = null;
            var mappingState = new ChatCompletionsStreamMappingState();

            await foreach (var update in modelProvider.CompleteChatStreamingAsync(responseRequest, cancellationToken))
            {
                CaptureStreamTail(
                    update,
                    ref activeId,
                    ref activeModel,
                    ref lastTimestamp,
                    ref lastFinishReason,
                    ref inputTokens,
                    ref outputTokens,
                    ref totalTokens,
                    ref rawUsage);

                foreach (var mapped in update.ToUnifiedStreamEvents(modelProvider.GetIdentifier(), mappingState))
                {
                    foreach (var streamEvent in ProcessStreamEvent(mapped, update))
                        yield return streamEvent;
                }

                if (rawChunkMapper is null)
                    continue;

                foreach (var extraEvent in rawChunkMapper(update))
                {
                    foreach (var streamEvent in ProcessStreamEvent(extraEvent, update))
                        yield return streamEvent;
                }
            }

            foreach (var tail in ChatCompletionsUnifiedMapper.FinalizeUnifiedStreamEvents(
                         modelProvider.GetIdentifier(),
                         mappingState,
                         DateTimeOffset.UtcNow))
            {
                yield return tail;
            }

            if (pendingFinish is not null)
            {
                var finishData = pendingFinish.Event.Data as AIFinishEventData;
                var finishTimestamp = pendingFinish.Event.Timestamp ?? lastTimestamp ?? DateTimeOffset.UtcNow;

                yield return CreateSyntheticFinishStreamEvent(
                    providerId: modelProvider.GetIdentifier(),
                    id: pendingFinish.Event.Id ?? activeId,
                    timestamp: finishTimestamp,
                    metadata: pendingFinish.Metadata ?? lastMetadata,
                    finishReason: finishData?.FinishReason ?? lastFinishReason ?? "stop",
                    model: finishData?.Model
                        ?? activeModel
                         ?? responseRequest.Model?.ToModelId(modelProvider.GetIdentifier()),
                        inputTokens: finishData?.InputTokens ?? inputTokens,
                        outputTokens: finishData?.OutputTokens ?? outputTokens,
                        totalTokens: finishData?.TotalTokens ?? totalTokens,
                        completedAt: finishData?.CompletedAt,
                        rawUsage: rawUsage,
                        messageMetadata: finishData?.MessageMetadata);

                yield break;
            }

            if (!finishObserved && (activeId is not null || activeModel is not null || lastTimestamp is not null))
            {
                var finishTimestamp = lastTimestamp ?? DateTimeOffset.UtcNow;

                if (reasoningStarted)
                {
                    reasoningStarted = false;
                    var reasoningEndId = activeReasoningSpanId ?? activeId;
                    activeReasoningSpanId = null;
                    yield return CreateSyntheticStreamEvent(
                        providerId: modelProvider.GetIdentifier(),
                        type: "reasoning-end",
                        id: reasoningEndId,
                        timestamp: finishTimestamp,
                        metadata: lastMetadata);
                }

                if (textStarted)
                {
                    textStarted = false;
                    var textEndId = activeTextSpanId ?? activeId;
                    activeTextSpanId = null;
                    yield return CreateSyntheticStreamEvent(
                        providerId: modelProvider.GetIdentifier(),
                        type: "text-end",
                        id: textEndId,
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
                    totalTokens: totalTokens,
                    rawUsage: rawUsage);
            }

            IEnumerable<AIStreamEvent> ProcessStreamEvent(AIStreamEvent streamEvent, ChatCompletionUpdate update)
            {
                var normalizedType = NormalizeEventType(streamEvent.Event.Type);
                var providerEventId = streamEvent.Event.Id ?? update.Id;

                if (!string.IsNullOrWhiteSpace(providerEventId))
                    activeId = providerEventId;

                if (!string.IsNullOrWhiteSpace(update.Model))
                    activeModel = update.Model;

                if (streamEvent.Event.Timestamp is not null
                    && (update.Created > 0 || normalizedType != "error"))
                {
                    lastTimestamp = streamEvent.Event.Timestamp;
                }

                lastMetadata = streamEvent.Metadata ?? lastMetadata;

                var stabilizedEvent = PinActiveSpanEventId(
                    streamEvent,
                    normalizedType,
                    providerEventId,
                    ref activeTextSpanId,
                    ref activeReasoningSpanId,
                    fallbackId: activeId);

                if (normalizedType == "reasoning-delta" && !reasoningStarted)
                {
                    if (textStarted)
                    {
                        textStarted = false;
                        var textEndId = activeTextSpanId ?? providerEventId ?? activeId;
                        activeTextSpanId = null;
                        yield return CreateSyntheticStreamEvent(
                            providerId: modelProvider.GetIdentifier(),
                            type: "text-end",
                            id: textEndId,
                            timestamp: stabilizedEvent.Event.Timestamp,
                            metadata: stabilizedEvent.Metadata);
                    }

                    reasoningStarted = true;
                    yield return CreateSyntheticStreamEvent(
                        providerId: modelProvider.GetIdentifier(),
                        type: "reasoning-start",
                        id: activeReasoningSpanId,
                        timestamp: stabilizedEvent.Event.Timestamp,
                        metadata: stabilizedEvent.Metadata);
                }

                if (normalizedType == "text-delta" && !textStarted)
                {
                    if (reasoningStarted)
                    {
                        reasoningStarted = false;
                        var reasoningEndId = activeReasoningSpanId ?? providerEventId ?? activeId;
                        activeReasoningSpanId = null;
                        yield return CreateSyntheticStreamEvent(
                            providerId: modelProvider.GetIdentifier(),
                            type: "reasoning-end",
                            id: reasoningEndId,
                            timestamp: stabilizedEvent.Event.Timestamp,
                            metadata: stabilizedEvent.Metadata);
                    }

                    textStarted = true;
                    yield return CreateSyntheticStreamEvent(
                        providerId: modelProvider.GetIdentifier(),
                        type: "text-start",
                        id: activeTextSpanId,
                        timestamp: stabilizedEvent.Event.Timestamp,
                        metadata: stabilizedEvent.Metadata);
                }

                if (normalizedType == "reasoning-start")
                    reasoningStarted = true;

                if (normalizedType == "reasoning-end")
                {
                    reasoningStarted = false;
                    activeReasoningSpanId = null;
                }

                if (normalizedType == "text-start")
                    textStarted = true;

                if (normalizedType == "text-end")
                {
                    textStarted = false;
                    activeTextSpanId = null;
                }

                if (normalizedType == "finish")
                {
                    finishObserved = true;

                    if (reasoningStarted)
                    {
                        reasoningStarted = false;
                        var reasoningEndId = activeReasoningSpanId ?? providerEventId ?? activeId;
                        activeReasoningSpanId = null;
                        yield return CreateSyntheticStreamEvent(
                            providerId: modelProvider.GetIdentifier(),
                            type: "reasoning-end",
                            id: reasoningEndId,
                            timestamp: stabilizedEvent.Event.Timestamp,
                            metadata: stabilizedEvent.Metadata);
                    }

                    if (textStarted)
                    {
                        textStarted = false;
                        var textEndId = activeTextSpanId ?? providerEventId ?? activeId;
                        activeTextSpanId = null;
                        yield return CreateSyntheticStreamEvent(
                            providerId: modelProvider.GetIdentifier(),
                            type: "text-end",
                            id: textEndId,
                            timestamp: stabilizedEvent.Event.Timestamp,
                            metadata: stabilizedEvent.Metadata);
                    }

                    pendingFinish = stabilizedEvent;
                    yield break;
                }

                yield return stabilizedEvent;
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

    private static AIStreamEvent PinActiveSpanEventId(
        AIStreamEvent streamEvent,
        string normalizedType,
        string? providerEventId,
        ref string? activeTextSpanId,
        ref string? activeReasoningSpanId,
        string? fallbackId)
    {
        if (IsTextSpanEventType(normalizedType))
        {
            var pinnedId = ResolvePinnedSpanEventId(
                normalizedType,
                providerEventId,
                fallbackId,
                ref activeTextSpanId);

            return RewriteStreamEventId(streamEvent, pinnedId);
        }

        if (IsReasoningSpanEventType(normalizedType))
        {
            var pinnedId = ResolvePinnedSpanEventId(
                normalizedType,
                providerEventId,
                fallbackId,
                ref activeReasoningSpanId);

            return RewriteStreamEventId(streamEvent, pinnedId);
        }

        return streamEvent;
    }

    private static string? ResolvePinnedSpanEventId(
        string normalizedType,
        string? providerEventId,
        string? fallbackId,
        ref string? activeSpanId)
    {
        if (normalizedType is "text-start" or "text-delta" or "reasoning-start" or "reasoning-delta")
            return EnsureActiveSpanId(ref activeSpanId, providerEventId, fallbackId);

        return activeSpanId ?? providerEventId ?? fallbackId;
    }

    private static string? EnsureActiveSpanId(ref string? activeSpanId, string? providerEventId, string? fallbackId)
    {
        if (string.IsNullOrWhiteSpace(activeSpanId))
            activeSpanId = !string.IsNullOrWhiteSpace(providerEventId) ? providerEventId : fallbackId;

        return activeSpanId;
    }

    private static bool IsTextSpanEventType(string normalizedType)
        => normalizedType is "text-start" or "text-delta" or "text-end";

    private static bool IsReasoningSpanEventType(string normalizedType)
        => normalizedType is "reasoning-start" or "reasoning-delta" or "reasoning-end";

    private static AIStreamEvent RewriteStreamEventId(AIStreamEvent streamEvent, string? id)
    {
        if (string.Equals(streamEvent.Event.Id, id, StringComparison.Ordinal))
            return streamEvent;

        return new AIStreamEvent
        {
            ProviderId = streamEvent.ProviderId,
            Metadata = streamEvent.Metadata,
            Event = new AIEventEnvelope
            {
                Type = streamEvent.Event.Type,
                Id = id,
                Timestamp = streamEvent.Event.Timestamp,
                Input = streamEvent.Event.Input,
                Output = streamEvent.Event.Output,
                Data = streamEvent.Event.Data,
                Metadata = streamEvent.Event.Metadata
            }
        };
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
        int? totalTokens,
        object? completedAt = null,
        object? rawUsage = null,
        AIFinishMessageMetadata? messageMetadata = null)
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
                    CompletedAt = completedAt ?? timestamp.ToUnixTimeSeconds(),
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = totalTokens ?? ((inputTokens ?? 0) + (outputTokens ?? 0)),
                    MessageMetadata = BuildFinishMessageMetadata(
                        messageMetadata,
                        model,
                        timestamp,
                        rawUsage,
                        inputTokens,
                        outputTokens,
                        totalTokens,
                        providerId)
                }
            },
            Metadata = metadata
        };

    private static AIFinishMessageMetadata? BuildFinishMessageMetadata(
        AIFinishMessageMetadata? existingMessageMetadata,
        string? model,
        DateTimeOffset timestamp,
        object? rawUsage,
        int? inputTokens,
        int? outputTokens,
        int? totalTokens,
        string? providerId = null)
    {
        if (existingMessageMetadata is null
            && rawUsage is null
            && inputTokens is null
            && outputTokens is null
            && totalTokens is null
            && string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var metadata = existingMessageMetadata?.ToDictionary() ?? [];

        if (!string.IsNullOrWhiteSpace(model))
            metadata["model"] = model;

        metadata["timestamp"] = timestamp;

        if (rawUsage is not null)
            metadata["usage"] = CloneUsageObject(rawUsage);

        if (!HasGatewayCost(metadata) && TryGetUsageCost(rawUsage, out var usageCost, providerId))
        {
            metadata["gateway"] = new Dictionary<string, object?>
            {
                ["cost"] = usageCost
            };
        }

        if (inputTokens is not null)
            metadata["inputTokens"] = inputTokens;

        if (outputTokens is not null)
            metadata["outputTokens"] = outputTokens;

        if (totalTokens is not null || inputTokens is not null || outputTokens is not null)
            metadata["totalTokens"] = totalTokens ?? ((inputTokens ?? 0) + (outputTokens ?? 0));

        return AIFinishMessageMetadata.FromDictionary(metadata, fallbackModel: model, fallbackTimestamp: timestamp);
    }

    private static object CloneUsageObject(object rawUsage)
        => rawUsage switch
        {
            JsonElement json => json.Clone(),
            Dictionary<string, object?> dictionary => dictionary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            _ => JsonSerializer.SerializeToElement(rawUsage, JsonSerializerOptions.Web)
        };

    private static bool HasGatewayCost(Dictionary<string, object?> metadata)
    {
        if (!metadata.TryGetValue("gateway", out var gateway) || gateway is null)
            return false;

        var gatewayElement = gateway switch
        {
            JsonElement json => json,
            _ => JsonSerializer.SerializeToElement(gateway, JsonSerializerOptions.Web)
        };

        return TryGetProperty(gatewayElement, "cost", out var costElement)
            && TryGetDecimal(costElement, out _);
    }

    private static bool TryGetUsageCost(object? rawUsage, out decimal cost, string? providerId = null)
    {
        cost = 0m;

        if (rawUsage is null)
            return false;

        var usageElement = rawUsage switch
        {
            JsonElement json => json,
            _ => JsonSerializer.SerializeToElement(rawUsage, JsonSerializerOptions.Web)
        };

        if (!TryGetProperty(usageElement, "cost", out var costElement))
            return false;

        if (!TryGetDecimal(costElement, out cost))
            return false;

        if (string.Equals(providerId, "cortecs", StringComparison.OrdinalIgnoreCase))
            cost /= 1_000_000m;

        return true;
    }

    private static bool TryGetDecimal(JsonElement element, out decimal value)
    {
        value = 0m;

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var parsed) => (value = parsed) >= 0 || parsed < 0,
            JsonValueKind.String when decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => (value = parsed) >= 0 || parsed < 0,
            _ => false
        };
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static void CaptureStreamTail(
        ChatCompletionUpdate update,
        ref string? activeId,
        ref string? activeModel,
        ref DateTimeOffset? lastTimestamp,
        ref string? lastFinishReason,
        ref int? inputTokens,
        ref int? outputTokens,
        ref int? totalTokens,
        ref object? rawUsage)
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
            rawUsage = usage.Clone();

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


}

