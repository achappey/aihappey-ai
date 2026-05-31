using System.Runtime.CompilerServices;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderMessagesUnifiedExtensions
{
     public static async Task<MessagesResponse> GetMessage(
          this IModelProvider modelProvider,
          HttpClient client,
          MessagesRequest options,
          string relativeUrl = "v1/messages",
          Dictionary<string, string>? headers = null,
          Abstractions.Http.ProviderBackendCaptureRequest? capture = null,
          CancellationToken cancellationToken = default)
    {
        return await client.PostMessages(options,
            modelProvider.GetIdentifier(),
            headers,
            relativeUrl,
            capture: capture,
            ct: cancellationToken);

    }


    public static async IAsyncEnumerable<MessageStreamPart> GetMessages(
        this IModelProvider modelProvider,
        HttpClient client,
        MessagesRequest options,
        string relativeUrl = "v1/messages",
        Dictionary<string, string>? headers = null,
        Abstractions.Http.ProviderBackendCaptureRequest? capture = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in client.PostMessagesStreaming(options,
            modelProvider.GetIdentifier(),
            headers,
            relativeUrl: relativeUrl,
            capture: capture,
            ct: cancellationToken))
            yield return update;

    }



    public static async Task<AIResponse> ExecuteUnifiedViaMessagesAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var messageRequest = request.ToMessagesRequest(modelProvider.GetIdentifier());
        messageRequest.Stream = false;

        var response = await modelProvider.MessagesAsync(messageRequest, request.Headers ?? [], cancellationToken);

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
        var isOpenCode = OpenCodeStreamingCostExtensions.IsOpenCodeProvider(modelProvider.GetIdentifier());
        decimal? latestGatewayCost = null;
        AIStreamEvent? pendingFinish = null;

        await foreach (var part in modelProvider.MessagesStreamingAsync(messageRequest, request.Headers ?? [], cancellationToken))
        {
            if (part is null)
                continue;

            if (isOpenCode && OpenCodeStreamingCostExtensions.TryGetOpenCodePingCost(part, out var pingCost))
                latestGatewayCost = pingCost;

            foreach (var mapped in part.ToUnifiedStreamEvents(modelProvider.GetIdentifier(), state))
            {
                if (!isOpenCode)
                {
                    yield return mapped;
                    continue;
                }

                if (string.Equals(mapped.Event.Type, "finish", StringComparison.OrdinalIgnoreCase))
                {
                    if (pendingFinish is not null)
                        yield return OpenCodeStreamingCostExtensions.ApplyGatewayCostToFinishEvent(pendingFinish, latestGatewayCost);

                    pendingFinish = mapped;
                    continue;
                }

                if (pendingFinish is not null)
                {
                    yield return OpenCodeStreamingCostExtensions.ApplyGatewayCostToFinishEvent(pendingFinish, latestGatewayCost);
                    pendingFinish = null;
                }

                yield return mapped;
            }
        }

        if (pendingFinish is not null)
            yield return OpenCodeStreamingCostExtensions.ApplyGatewayCostToFinishEvent(pendingFinish, latestGatewayCost);

    }
}
