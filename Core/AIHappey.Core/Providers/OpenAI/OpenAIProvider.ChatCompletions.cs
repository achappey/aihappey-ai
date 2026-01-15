using OpenAI;
using OAIC = OpenAI.Chat;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!_client.DefaultRequestHeaders.Contains("Authorization"))
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GetKey());

        options.ParallelToolCalls ??= true;

        return _client.GetChatCompletionUpdates(
                   options, ct: cancellationToken);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions chatRequest, CancellationToken cancellationToken = default)
    {
        if (!_client.DefaultRequestHeaders.Contains("Authorization"))
            _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GetKey());

        chatRequest.ParallelToolCalls ??= true;

        return await _client.GetChatCompletion(
           chatRequest, ct: cancellationToken);
    }
}
