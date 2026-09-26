using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.GMICloud;

public sealed partial class GMICloudProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (TryGetAutorouteMode(options.Model, out var mode))
            return await CompleteAutorouteAsync(options, mode, cancellationToken);

        return await this.GetChatCompletion(_client,
             options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (TryGetAutorouteMode(options.Model, out var mode))
            return StreamAutorouteAsync(options, mode, cancellationToken);

        return this.GetChatCompletions(_client,
                    options, cancellationToken: cancellationToken);
    }
}

