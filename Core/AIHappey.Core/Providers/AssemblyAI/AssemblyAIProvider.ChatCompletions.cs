using AIHappey.Core.AI;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.Providers.AssemblyAI;

public partial class AssemblyAIProvider
{

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(_llmGatewayClient);

        return await this.GetChatCompletion(_llmGatewayClient,
             options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(_llmGatewayClient);

        return this.GetChatCompletions(_llmGatewayClient,
                    options, cancellationToken: cancellationToken);
    }

}


