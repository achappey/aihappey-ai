using System.Runtime.CompilerServices;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.ChainGPT;

public partial class ChainGPTProvider
{
    private async Task<ChatCompletion> CompleteChatInternalAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var prompt = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("No prompt provided.", nameof(options));

        var answer = await CompleteQuestionBufferedAsync(modelId, prompt, metadata: null, cancellationToken);

        return new ChatCompletion
        {
            Id = Guid.NewGuid().ToString("n"),
            Object = "chat.completion",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = modelId,
            Choices =
            [
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = answer },
                    finish_reason = "stop"
                }
            ],
            Usage = null
        };
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingInternalAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var prompt = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("No prompt provided.", nameof(options));

        var id = Guid.NewGuid().ToString("n");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = modelId,
            Choices =
            [
                new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
            ]
        };

        await foreach (var chunk in CompleteQuestionStreamingAsync(modelId, prompt, metadata: null, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(chunk))
                continue;

            yield return new ChatCompletionUpdate
            {
                Id = id,
                Created = created,
                Model = modelId,
                Choices =
                [
                    new { index = 0, delta = new { content = chunk }, finish_reason = (string?)null }
                ]
            };
        }

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = modelId,
            Choices =
            [
                new { index = 0, delta = new { }, finish_reason = "stop" }
            ]
        };
    }
}
