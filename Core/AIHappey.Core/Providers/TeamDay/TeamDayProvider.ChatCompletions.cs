using System.Runtime.CompilerServices;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.TeamDay;

public partial class TeamDayProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var model = NormalizeAgentModelId(options.Model);
        var prompt = ApplyStructuredOutputInstructions(BuildPromptFromCompletionMessages(options.Messages), options.ResponseFormat);
        var result = await ExecuteAgentAsync(model, prompt, metadata: null, stream: false, cancellationToken);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new ChatCompletion
        {
            Id = result.ExecutionId,
            Object = "chat.completion",
            Created = createdAt,
            Model = options.Model,
            Usage = result.Usage,
            Choices =
            [
                new
                {
                    index = 0,
                    finish_reason = "stop",
                    message = new
                    {
                        role = "assistant",
                        content = result.Text
                    }
                }
            ]
        };
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var model = NormalizeAgentModelId(options.Model);
        var prompt = ApplyStructuredOutputInstructions(BuildPromptFromCompletionMessages(options.Messages), options.ResponseFormat);
        var responseId = Guid.NewGuid().ToString("n");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        object? usage = null;
        string? failureMessage = null;

        await foreach (var evt in StreamAgentExecutionAsync(model, prompt, metadata: null, cancellationToken))
        {
            switch (evt)
            {
                case TeamDayMetaStreamEvent meta:
                    if (!string.IsNullOrWhiteSpace(meta.ExecutionId))
                        responseId = meta.ExecutionId!;
                    break;

                case TeamDayDeltaStreamEvent delta when !string.IsNullOrEmpty(delta.Text):
                    yield return new ChatCompletionUpdate
                    {
                        Id = responseId,
                        Object = "chat.completion.chunk",
                        Created = createdAt,
                        Model = options.Model,
                        Choices =
                        [
                            new
                            {
                                index = 0,
                                delta = new
                                {
                                    role = "assistant",
                                    content = delta.Text
                                },
                                finish_reason = (string?)null
                            }
                        ]
                    };
                    break;

                case TeamDayResultStreamEvent result:
                    if (result.Usage is not null)
                        usage = result.Usage;
                    break;

                case TeamDayErrorStreamEvent error:
                    failureMessage = error.Message;
                    break;
            }
        }

        yield return new ChatCompletionUpdate
        {
            Id = responseId,
            Object = "chat.completion.chunk",
            Created = createdAt,
            Model = options.Model,
            Usage = usage,
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = failureMessage is null ? "stop" : "error"
                }
            ]
        };
    }


    private static string BuildPromptFromCompletionMessages(IEnumerable<ChatMessage>? messages)
    {
        var lines = new List<string>();

        foreach (var message in messages ?? [])
        {
            var content = FlattenCompletionMessageContent(message.Content);
            if (!string.IsNullOrWhiteSpace(content))
                lines.Add($"{message.Role}: {content}");
        }

        return string.Join("\n\n", lines);
    }


}
