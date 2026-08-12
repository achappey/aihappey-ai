using AIHappey.ChatCompletions.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider : IModelProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        if (IsOcrModel(options.Model))
            return (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToChatCompletion();

        ApplyAuthHeader();

        this.SetDefaultChatCompletionProperties(options);

        return await this.GetChatCompletion(_client,
             options, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsOcrModel(options.Model))
        {
            await foreach (var item in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
                yield return item.ToChatCompletionUpdate();
            yield break;
        }

        ApplyAuthHeader();

        this.SetDefaultChatCompletionProperties(options);

        await foreach (var item in this.GetChatCompletions(_client, options, cancellationToken: cancellationToken))
            yield return item;
    }
}
