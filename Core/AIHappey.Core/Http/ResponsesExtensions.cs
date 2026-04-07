using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

public static class ResponsesExtensions
{
    /*public static async IAsyncEnumerable<UIMessagePart> ResponsesStreamAsync(
        this HttpClient client,
        ChatRequest chatRequest,
        string providerId,
        ResponsesChatStreamOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(chatRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        options ??= new ResponsesChatStreamOptions();

        ResponseRequest? request = null;
        JsonElement? extraRootProperties = null;
        UIMessagePart? requestErrorPart = null;

        try
        {
            request = options.RequestFactory?.Invoke(chatRequest)
                ?? chatRequest.ToResponsesRequest(providerId, options.RequestMappingOptions);

            request.Stream ??= true;
            request.Store ??= false;
            options.RequestMutator?.Invoke(request);
            extraRootProperties = options.ExtraRootPropertiesFactory?.Invoke(chatRequest);
        }
        catch (Exception ex)
        {
            if (!options.EmitErrorPartOnException)
                throw;

            requestErrorPart = options.ExceptionMapper?.Invoke(ex) ?? ex.Message.ToErrorUIPart();
        }

        if (requestErrorPart != null)
        {
            yield return requestErrorPart;
            yield break;
        }

        var context = new ResponsesStreamMappingContext(options.StreamMappingOptions);

        await foreach (var update in client.GetResponsesUpdates(
            request!,
            relativeUrl: options.Url,
            ct: cancellationToken,
            extraRootProperties: extraRootProperties))
        {
            await foreach (var part in update.ToUIMessagePartsAsync(providerId, context, cancellationToken))
            {
                if (options.PartPostProcessor != null)
                {
                    await foreach (var mapped in options.PartPostProcessor(part, cancellationToken))
                        yield return mapped;
                }
                else
                {
                    yield return part;
                }
            }
        }
    }*/
}
