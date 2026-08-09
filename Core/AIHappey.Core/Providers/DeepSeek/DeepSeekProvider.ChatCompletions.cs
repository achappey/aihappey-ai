using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.DeepSeek;

public sealed partial class DeepSeekProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var response = await this.GetChatCompletion(_client,
             options,
             relativeUrl: "chat/completions",
             cancellationToken: cancellationToken);

        return this.EnrichChatCompletionWithCatalogGatewayCost(response, options.Model);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        string? lastFinishReason = null;
        await foreach (var update in this.GetChatCompletions(_client,
                           options,
                           relativeUrl: "chat/completions",
                           cancellationToken: cancellationToken))
        {
            CatalogPricingCostingExtensions.NormalizeStreamingUpdateForGatewayCost(update, ref lastFinishReason);
            yield return this.EnrichChatCompletionUpdateWithCatalogGatewayCost(update, options.Model);
        }
    }
}

