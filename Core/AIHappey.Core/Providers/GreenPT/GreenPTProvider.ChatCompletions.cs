using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.ChatCompletions.Mapping;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.GreenPT;

public partial class GreenPTProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        if (IsSpecialUnifiedModel(options.Model))
            return (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToChatCompletion();

        ApplyAuthHeader();

        return await this.GetChatCompletion(_client,
             options, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsSpecialUnifiedModel(options.Model))
        {
            await foreach (var item in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
                yield return item.ToChatCompletionUpdate();
            yield break;
        }

        ApplyAuthHeader();

        await foreach (var item in this.GetChatCompletions(_client,
                           options, cancellationToken: cancellationToken))
            yield return item;
    }

    private static bool IsSpecialUnifiedModel(string? model)
        => IsTranscriptionModel(model) || IsOcrModel(model) || IsWebSearchModel(model);

}

