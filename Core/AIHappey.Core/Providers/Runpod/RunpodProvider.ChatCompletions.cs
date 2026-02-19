using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.Runpod;

public partial class RunpodProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
    
    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
