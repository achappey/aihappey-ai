using ANT = Anthropic.SDK;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider
{
    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<global::OpenAI.Chat.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<UIMessagePart> CompleteAsync(ChatCompletionOptions chatRequest,
     CancellationToken cancellationToken = default)
    {
        var client = new ANT.AnthropicClient(
            GetKey(),
            client: _client
        );
        
        throw new NotImplementedException();
    }
}