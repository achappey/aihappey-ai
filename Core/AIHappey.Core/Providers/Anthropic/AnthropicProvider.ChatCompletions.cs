using ANT = Anthropic.SDK;
using AIHappey.ChatCompletions.Models;
using AIHappey.Vercel.Models;
using AIHappey.ChatCompletions.Mapping;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider
{

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions chatRequest,
     CancellationToken cancellationToken = default)
    {
        var result = await this.ExecuteUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToChatCompletion();
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
     [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {

            yield return part.ToChatCompletionUpdate();

        }

        yield break;
    }

}