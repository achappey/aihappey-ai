using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        options.ParallelToolCalls ??= true;

        this.SetDefaultChatCompletionProperties(options, ["native_mcp"]);

        return _client.GetChatCompletionUpdates(
                   options, ct: cancellationToken);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions chatRequest, CancellationToken cancellationToken = default)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        chatRequest.ParallelToolCalls ??= true;

        this.SetDefaultChatCompletionProperties(chatRequest, ["native_mcp"]);

        return await _client.GetChatCompletion(
           chatRequest, ct: cancellationToken);
    }
}
