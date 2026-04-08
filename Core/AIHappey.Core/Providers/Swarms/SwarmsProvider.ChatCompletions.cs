using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.Providers.Swarms;

public partial class SwarmsProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var prompt = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Swarms requires non-empty chat input.");

        var executed = await ExecuteCompletionAsync(
            options.Model,
            prompt,
            BuildHistoryFromChatMessages(options.Messages),
            ExtractSystemPrompt(options.Messages),
            options.Temperature,
            null,
            cancellationToken);

        return new ChatCompletion
        {
            Id = executed.Id,
            Object = "chat.completion",
            Created = executed.CreatedAt,
            Model = options.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = executed.Text
                    },
                    finish_reason = "stop"
                }
            ],
            Usage = executed.Usage
        };
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var prompt = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Swarms requires non-empty chat input.");

        var itemId = Guid.NewGuid().ToString("n");

        await foreach (var delta in ExecuteCompletionStreamingAsync(
                           options.Model,
                           prompt,
                           BuildHistoryFromChatMessages(options.Messages),
                           ExtractSystemPrompt(options.Messages),
                           options.Temperature,
                           null,
                           cancellationToken))
        {
            yield return new ChatCompletionUpdate
            {
                Id = itemId,
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = options.Model,
                Choices =
                [
                    new
                    {
                        index = 0,
                        delta = new { content = delta }
                    }
                ]
            };
        }

        yield return new ChatCompletionUpdate
        {
            Id = itemId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
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
