using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.AI21;

public sealed partial class AI21Provider
{

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        if (IsMaestroModel(options.Model))
            return (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToChatCompletion();

        ApplyAuthHeader();

        var response = await this.GetChatCompletion(_client,
              options, cancellationToken: cancellationToken);

        return EnrichChatCompletionWithGatewayCost(response, options.Model);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsMaestroModel(options.Model))
        {
            await foreach (var update in StreamMaestroChatCompletionUpdatesAsync(options, cancellationToken))
                yield return update;

            yield break;
        }

        ApplyAuthHeader();

        await foreach (var update in this.GetChatCompletions(_client,
                           options,
                           cancellationToken: cancellationToken))
        {
            yield return EnrichChatCompletionUpdateWithGatewayCost(update, options.Model);
        }
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> StreamMaestroChatCompletionUpdatesAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var streamEvent in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            yield return streamEvent.ToChatCompletionUpdate();
    }

}

