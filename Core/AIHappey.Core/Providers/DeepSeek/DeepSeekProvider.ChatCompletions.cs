using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.DeepSeek;

public sealed partial class DeepSeekProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await _client.GetChatCompletion(
             options,
             relativeUrl: "chat/completions",
             ct: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.GetChatCompletionUpdates(
                    options,
                    relativeUrl: "chat/completions",
                    ct: cancellationToken);
    }
}

