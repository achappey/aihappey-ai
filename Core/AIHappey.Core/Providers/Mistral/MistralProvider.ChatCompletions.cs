using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider : IModelProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        this.SetDefaultChatCompletionProperties(options);

        return await _client.GetChatCompletion(
             options, ct: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        this.SetDefaultChatCompletionProperties(options);

        return _client.GetChatCompletionUpdates(
                    options, ct: cancellationToken);
    }
}