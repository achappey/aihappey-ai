using System.Runtime.CompilerServices;
using AIHappey.Core.Contracts;
using AIHappey.Messages.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderMessagesUnifiedExtensions
{
    public static async Task<AIResponse> ExecuteUnifiedViaMessagesAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var messageRequest = request.ToMessagesRequest(modelProvider.GetIdentifier());
        messageRequest.Stream = false;

        var response = await modelProvider.MessagesAsync(messageRequest, request.Headers ?? new Dictionary<string, string>(), cancellationToken);

        return response.ToUnifiedResponse(modelProvider.GetIdentifier());

    }

    public static async IAsyncEnumerable<AIStreamEvent> StreamUnifiedViaMessagesAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var messageRequest = request.ToMessagesRequest(modelProvider.GetIdentifier());
        messageRequest.Stream = true;

        var state = new MessagesUnifiedMapper.MessagesStreamMappingState();

        yield return new AIStreamEvent
        {
            ProviderId = modelProvider.GetIdentifier(),
            Event = new AIEventEnvelope
            {
                Type = "data-messages.request",
                Timestamp = DateTimeOffset.UtcNow,
                Data = new AIDataEventData
                {
                    Data = messageRequest
                }
            },
            Metadata = request.Headers is null || request.Headers.Count == 0
                ? null
                : new Dictionary<string, object?>
                {
                    ["unified.request.headers"] = request.Headers.ToDictionary(a => a.Key, a => (object?)a.Value)
                }
        };

        await foreach (var part in modelProvider.MessagesStreamingAsync(messageRequest, request.Headers ?? new Dictionary<string, string>(), cancellationToken))
        {
            if (part is null)
                continue;

            foreach (var mapped in part.ToUnifiedStreamEvents(modelProvider.GetIdentifier(), state))
                yield return mapped;
        }

    }
}
