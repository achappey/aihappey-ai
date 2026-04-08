using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Core.Contracts;
using AIHappey.Responses;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderResponsesChatExtensions
{
    public static void SetDefaultResponseProperties(
        this IModelProvider modelProvider, ResponseRequest responseRequest)
    {
        responseRequest.Tools = [.. responseRequest.Tools ?? [],
            .. responseRequest.Metadata.GetResponseToolDefinitions(modelProvider.GetIdentifier()) ?? []];

        responseRequest.Reasoning ??= responseRequest.Metadata
            .GetProviderOption<Responses.Reasoning>(modelProvider.GetIdentifier(), "reasoning");
        responseRequest.Include ??= responseRequest.Metadata
            .GetProviderOption<List<string>>(modelProvider.GetIdentifier(), "include");


        responseRequest.Metadata = null;
    }

    public static async IAsyncEnumerable<UIMessagePart> StreamResponsesAsync(
        this IModelProvider modelProvider,
        ChatRequest chatRequest,
        Func<ChatRequest, CancellationToken, ValueTask<ResponseRequest>>? requestFactory = null,
        Func<ChatRequest, ResponsesStreamMappingOptions?>? mappingOptionsFactory = null,
        Func<UIMessagePart, ChatRequest, CancellationToken, IAsyncEnumerable<UIMessagePart>>? partPostProcessor = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(chatRequest);

        var providerId = modelProvider.GetIdentifier();

        ResponseRequest? request = null;
        UIMessagePart? requestErrorPart = null;

        try
        {
            request = requestFactory != null
                ? await requestFactory(chatRequest, cancellationToken)
                : chatRequest.ToResponsesRequest(providerId);

            request.Stream ??= true;
            request.Store ??= false;
        }
        catch (Exception ex)
        {
            requestErrorPart = ex.Message.ToErrorUIPart();
        }

        if (requestErrorPart != null)
        {
            yield return requestErrorPart;
            yield break;
        }

        var context = new ResponsesStreamMappingContext(mappingOptionsFactory?.Invoke(chatRequest));

        await foreach (var update in modelProvider.ResponsesStreamingAsync(request!, cancellationToken))
        {
            await foreach (var part in update.ToUIMessagePartsAsync(providerId, context, cancellationToken))
            {
                if (partPostProcessor != null)
                {
                    await foreach (var mapped in partPostProcessor(part, chatRequest, cancellationToken))
                        yield return mapped;
                }
                else
                {
                    yield return part;
                }
            }
        }
    }
}
