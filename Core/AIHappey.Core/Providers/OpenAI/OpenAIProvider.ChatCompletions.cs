using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        options.ParallelToolCalls ??= true;

        this.SetDefaultChatCompletionProperties(options);

        await foreach (var update in this.GetChatCompletions(
            _client,
            options,
            cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions chatRequest, CancellationToken cancellationToken = default)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        chatRequest.ParallelToolCalls ??= true;

        this.SetDefaultChatCompletionProperties(chatRequest);

        return await this.GetChatCompletion(_client,
           chatRequest, cancellationToken: cancellationToken);
    }
}
