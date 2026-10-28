using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Lightcone;

public sealed partial class LightconeProvider
{
    private Task<ChatCompletion> CompleteLightconeChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var providerHeaders = this.SetDefaultChatCompletionProperties(options);
        var headers = CreateAuthenticatedHeaders(providerHeaders, options.Headers);
        return _client.GetChatCompletion(
            options,
            GetIdentifier(),
            relativeUrl: "v1/chat/completions",
            headers: headers,
            ct: cancellationToken);
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> StreamLightconeChatAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var providerHeaders = this.SetDefaultChatCompletionProperties(options);
        var headers = CreateAuthenticatedHeaders(providerHeaders, options.Headers);

        await foreach (var update in _client.GetChatCompletionUpdates(
                           options,
                           GetIdentifier(),
                           relativeUrl: "v1/chat/completions",
                           headers: headers,
                           ct: cancellationToken))
        {
            yield return update;
        }
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var streamEvent in StreamUnifiedAsync(
                           chatRequest.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken))
        {
            foreach (var part in streamEvent.Event.ToUIMessagePart(GetIdentifier()))
                yield return part;
        }
    }

}
