using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Upstage;

public sealed partial class UpstageProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        options.Store = null;
        options.ToolChoice = options.Tools.Any() ? options.ToolChoice : null!;
        options.Tools = options.Tools.Any() ? options.Tools : null!;

        return await this.GetChatCompletion(_client,
             options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        options.Store = null;
        options.ToolChoice = options.Tools.Any() ? options.ToolChoice : null!;
        options.Tools = options.Tools.Any() ? options.Tools : null!;

        return this.GetChatCompletions(_client,
                    options, cancellationToken: cancellationToken);
    }


}

