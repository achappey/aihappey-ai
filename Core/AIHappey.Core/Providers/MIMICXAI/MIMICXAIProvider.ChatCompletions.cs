using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;
using AIHappey.ChatCompletions.Mapping;

namespace AIHappey.Core.Providers.MIMICXAI;

public partial class MIMICXAIProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToChatCompletion();

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        return StreamChatCompletionsAsync(options, cancellationToken);
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> StreamChatCompletionsAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var streamEvent in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            yield return streamEvent.ToChatCompletionUpdate();
    }
}
