using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.EUrouter;

public partial class EUrouterProvider
{

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var response = await this.GetChatCompletion(_client,
              options, cancellationToken: cancellationToken);

        return await EnrichChatCompletionWithGatewayCostAsync(response, options.Model, cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var update in this.GetChatCompletions(_client,
                         options,
                         cancellationToken: cancellationToken))
        {
            yield return await EnrichChatCompletionUpdateWithGatewayCostAsync(update, options.Model, cancellationToken);
        }
    }
}
