using AIHappey.Core.AI;
using AIHappey.ChatCompletions.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.IONOS;

public partial class IONOSProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetChatCompletion(_client, options, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var update in this.GetChatCompletions(_client, options, cancellationToken: cancellationToken))
            yield return update;

        yield break;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                yield return uiPart;
            }
        }

        yield break;
    }
}