using System.Runtime.CompilerServices;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.NinjaChat;

public partial class NinjaChatProvider
{

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (IsNativeSearchModel(options.Model))
            return await ExecuteNativeSearchChatCompletionAsync(options, cancellationToken);

        return await _client.GetChatCompletion(
             options,
             relativeUrl: "v1/chat",
             ct: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (IsNativeSearchModel(options.Model))
            return ExecuteNativeSearchChatCompletionStreamingAsync(options, cancellationToken);

        return _client.GetChatCompletionUpdates(
                    options,
                    relativeUrl: "v1/chat",
                    ct: cancellationToken);
    }

    private NinjaChatSearchRequest BuildNativeSearchRequest(ChatCompletionOptions options)
          => BuildNativeSearchRequest(
              query: BuildPromptFromCompletionMessages(options.Messages),
              passthrough: null);

    private async Task<ChatCompletion> ExecuteNativeSearchChatCompletionAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var execution = await ExecuteNativeSearchAsync(BuildNativeSearchRequest(options), cancellationToken);

        return new ChatCompletion
        {
            Id = execution.Id,
            Object = "chat.completion",
            Created = execution.CreatedAt,
            Model = options.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    finish_reason = "stop",
                    message = new
                    {
                        role = "assistant",
                        content = execution.Text
                    }
                }
            ]
        };
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> ExecuteNativeSearchChatCompletionStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var execution = await ExecuteNativeSearchAsync(BuildNativeSearchRequest(options), cancellationToken);

        foreach (var chunk in ChunkText(execution.Text))
        {
            yield return new ChatCompletionUpdate
            {
                Id = execution.Id,
                Created = execution.CreatedAt,
                Model = options.Model,
                Choices =
                [
                    new
                    {
                        index = 0,
                        delta = new { content = chunk }
                    }
                ]
            };
        }

        yield return new ChatCompletionUpdate
        {
            Id = execution.Id,
            Created = execution.CreatedAt,
            Model = options.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = "stop"
                }
            ]
        };
    }

}
