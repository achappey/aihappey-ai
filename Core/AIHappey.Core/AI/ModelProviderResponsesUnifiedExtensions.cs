using System.Runtime.CompilerServices;
using AIHappey.Core.Contracts;
using AIHappey.Unified.Models;
using AIHappey.Responses.Mapping;
using System.Text.Json;

namespace AIHappey.Core.AI;

public static class ModelProviderResponsesUnifiedExtensions
{
    public static async Task<AIResponse> ExecuteUnifiedViaResponsesAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var responseRequest = request.ToResponseRequest(modelProvider.GetIdentifier());
        responseRequest.Stream = false;
        responseRequest.Store ??= false;

        var response = await modelProvider.ResponsesAsync(responseRequest, cancellationToken);
        var unified = response.ToUnifiedResponse(modelProvider.GetIdentifier());

        return unified;
    }

    public static async IAsyncEnumerable<AIStreamEvent> StreamUnifiedViaResponsesAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var responseRequest = request.ToResponseRequest(modelProvider.GetIdentifier());
        responseRequest.Stream = true;
        responseRequest.Store ??= false;

        IAsyncEnumerable<AIStreamEvent> stream = StreamCore();

        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return streamEvent;

        async IAsyncEnumerable<AIStreamEvent> StreamCore()
        {
            yield return new AIStreamEvent
            {
                ProviderId = modelProvider.GetIdentifier(),
                Event = new AIEventEnvelope
                {
                    Type = "data-responses.request",
                    Timestamp = DateTimeOffset.UtcNow,
                    Data = new AIDataEventData
                    {
                        Data = JsonSerializer.SerializeToElement(responseRequest, JsonSerializerOptions.Web)
                    }
                },
                Metadata = request.Headers is null || request.Headers.Count == 0
                    ? null
                    : new Dictionary<string, object?>
                    {
                        ["unified.request.headers"] = request.Headers.ToDictionary(a => a.Key, a => (object?)a.Value)
                    }
            };

            await foreach (var update in modelProvider.ResponsesStreamingAsync(responseRequest, cancellationToken))
            {
                foreach (var evt in update.ToUnifiedStreamEvent(modelProvider.GetIdentifier()))
                {
                    yield return evt;
                }

            }
        }
    }
}

