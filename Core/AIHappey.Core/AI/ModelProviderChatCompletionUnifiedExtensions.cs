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

        var responseRequest = request.ToChatCompletionOptions();
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

        var responseRequest = request.ToChatCompletionOptions();
        responseRequest.Stream = true;
        responseRequest.Store ??= false;

        IAsyncEnumerable<AIStreamEvent> stream = StreamCore();

        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return streamEvent;

        async IAsyncEnumerable<AIStreamEvent> StreamCore()
        {
            var textStarted = false;
            var reasoningStarted = false;
            string? activeId = null;
            var mappingState = new ChatCompletionsUnifiedMapper.ChatCompletionsStreamMappingState();

            yield return CreateDataStreamEvent(
                modelProvider.GetIdentifier(),
                "data-chatcompletions.request",
                JsonSerializer.SerializeToElement(responseRequest, JsonSerializerOptions.Web),
                request.Headers);

            await foreach (var update in modelProvider.CompleteChatStreamingAsync(responseRequest, cancellationToken))
            {
                foreach (var mapped in update.ToUnifiedStreamEvents(modelProvider.GetIdentifier(), mappingState))
                {
                    var normalizedType = NormalizeEventType(mapped.Event.Type);
                    var eventId = mapped.Event.Id ?? update.Id;

                    if (!string.IsNullOrWhiteSpace(eventId))
                        activeId = eventId;

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

