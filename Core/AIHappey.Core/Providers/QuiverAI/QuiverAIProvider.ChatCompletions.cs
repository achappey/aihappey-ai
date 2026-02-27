using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.QuiverAI;

public partial class QuiverAIProvider
{
    private async Task<ChatCompletion> CompleteChatCoreAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        return await _client.GetChatCompletion(options, ct: cancellationToken);
    }

    private IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingCoreAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        return _client.GetChatCompletionUpdates(options, ct: cancellationToken);
    }
}

