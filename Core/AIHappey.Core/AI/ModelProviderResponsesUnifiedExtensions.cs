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
        Console.WriteLine(JsonSerializer.Serialize(response, JsonSerializerOptions.Web));
        var unified = response.ToUnifiedResponse(modelProvider.GetIdentifier());
        Console.WriteLine(JsonSerializer.Serialize(unified, JsonSerializerOptions.Web));

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
            await foreach (var update in modelProvider.ResponsesStreamingAsync(responseRequest, cancellationToken))
            {
                Console.WriteLine(JsonSerializer.Serialize(update, JsonSerializerOptions.Web));

                foreach (var evt in update.ToUnifiedStreamEvent(modelProvider.GetIdentifier()))
                {
                    Console.WriteLine(JsonSerializer.Serialize(evt, JsonSerializerOptions.Web));
                    yield return evt;
                }

            }
        }
    }
}

