using System.Runtime.CompilerServices;
using AIHappey.Core.Contracts;
using AIHappey.Unified.Models;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Extensions;

namespace AIHappey.Core.AI;

public static class ModelProviderResponsesUnifiedExtensions
{
    public static async Task<Responses.ResponseResult> GetResponse(
          this IModelProvider modelProvider,
          HttpClient client,
          Responses.ResponseRequest options,
          string relativeUrl = "v1/responses",
          System.Text.Json.JsonElement? extraRootProperties = null,
          Abstractions.Http.ProviderBackendCaptureRequest? capture = null,
          CancellationToken cancellationToken = default)
    {
        modelProvider.SetDefaultResponseProperties(options);

        return await client.GetResponses(options,
            modelProvider.GetIdentifier(),
            relativeUrl,
            extraRootProperties: extraRootProperties,
            capture: capture,
            ct: cancellationToken);

    }


    public static async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> GetResponses(
        this IModelProvider modelProvider,
        HttpClient client,
        Responses.ResponseRequest options,
        string relativeUrl = "v1/responses",
        System.Text.Json.JsonElement? extraRootProperties = null,
        Abstractions.Http.ProviderBackendCaptureRequest? capture = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        modelProvider.SetDefaultResponseProperties(options);

        await foreach (var update in client.GetResponsesUpdates(options,
            relativeUrl: relativeUrl,
            providerId: modelProvider.GetIdentifier(),
            extraRootProperties: extraRootProperties,
            capture: capture,
            ct: cancellationToken))
            yield return update;

    }


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
            var seenPerplexitySearchSourceUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await foreach (var update in modelProvider.ResponsesStreamingAsync(responseRequest, cancellationToken))
            {
                foreach (var evt in update.ToUnifiedStreamEvent(modelProvider.GetIdentifier()))
                {
                    if (ShouldSkipDuplicatePerplexitySearchSourceUrl(modelProvider.GetIdentifier(), evt, seenPerplexitySearchSourceUrls))
                        continue;

                    yield return evt;
                }

            }
        }
    }

    private static bool ShouldSkipDuplicatePerplexitySearchSourceUrl(
        string providerId,
        AIStreamEvent streamEvent,
        HashSet<string> seenSourceUrls)
    {
        if (!string.Equals(providerId, "perplexity", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(streamEvent.Event.Type, "source-url", StringComparison.OrdinalIgnoreCase)
            || streamEvent.Event.Data is not AISourceUrlEventData sourceEvent
            || !string.Equals(sourceEvent.Type, "search_results", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(sourceEvent.Url))
        {
            return false;
        }

        return !seenSourceUrls.Add(sourceEvent.Url);
    }
}

