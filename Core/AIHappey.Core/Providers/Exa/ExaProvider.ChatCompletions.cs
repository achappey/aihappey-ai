using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.Providers.Exa;

public partial class ExaProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken);

        return result.ToChatCompletion();
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in StreamUnifiedAsync(unifiedRequest, cancellationToken))
            yield return part.ToChatCompletionUpdate();
    }
}
