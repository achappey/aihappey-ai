using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Synexa;

public partial class SynexaProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToChatCompletion();

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var streamEvent in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            yield return streamEvent.ToChatCompletionUpdate();
    }
}

