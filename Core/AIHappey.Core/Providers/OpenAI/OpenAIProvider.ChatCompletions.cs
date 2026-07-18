using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(options.Model, cancellationToken);
        if (string.Equals(model.Type, "transcription", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var streamEvent in this.StreamUnifiedAsync(
                options.ToUnifiedRequest(GetIdentifier()),
                cancellationToken).WithCancellation(cancellationToken))
            {
                yield return streamEvent.ToChatCompletionUpdate();
            }

            yield break;
        }

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
        var model = await this.GetModel(chatRequest.Model, cancellationToken);
        if (string.Equals(model.Type, "transcription", StringComparison.OrdinalIgnoreCase))
        {
            var response = await this.ExecuteUnifiedAsync(
                chatRequest.ToUnifiedRequest(GetIdentifier()),
                cancellationToken);

            return response.ToChatCompletion();
        }

        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        chatRequest.ParallelToolCalls ??= true;

        this.SetDefaultChatCompletionProperties(chatRequest);

        return await this.GetChatCompletion(_client,
           chatRequest, cancellationToken: cancellationToken);
    }
}
