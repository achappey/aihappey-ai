using System.Runtime.CompilerServices;
using System.Text;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.Providers.YouCom;

public partial class YouComProvider
{
    private async Task<ChatCompletion> CompleteChatCoreAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var prompt = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("You.com requires non-empty chat input.");

        if (IsResearchModel(options.Model))
        {
            if (options.Tools?.Any() == true)
                throw new NotSupportedException("You.com research models do not support tool definitions. Use agent models for grounded agent behavior.");

            var result = await ExecuteResearchAsync(options.Model, prompt, options.ResponseFormat, null, cancellationToken);
            return ToChatCompletion(result);
        }

        if (!IsAgentModel(options.Model))
            throw new NotSupportedException($"Unsupported You.com chat model '{options.Model}'.");

        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var responseId = Guid.NewGuid().ToString("n");
        var text = new StringBuilder();
        var finishReason = "error";
        var runtimeMs = (long?)null;
        var error = default(string);

        await foreach (var evt in StreamAgentEventsAsync(options.Model, prompt, options.ResponseFormat, options.Tools?.Any() == true, new YouComRequestMetadata(), cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(evt.Delta))
                text.Append(evt.Delta);

            if (evt.Type == "response.done")
            {
                runtimeMs = evt.RuntimeMs;
                finishReason = evt.Finished == false ? "error" : "stop";
                if (evt.Finished == false)
                    error = "You.com agent run did not finish successfully.";
            }
        }

        return ToChatCompletion(new YouComExecutionResult
        {
            Id = responseId,
            Model = options.Model,
            Endpoint = "agents.runs",
            Text = text.ToString(),
            FinishReason = finishReason,
            CreatedAt = createdAt,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            RuntimeMs = runtimeMs,
            Error = error
        });
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatCoreStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("You.com requires non-empty chat input.");

        var responseId = Guid.NewGuid().ToString("n");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (IsResearchModel(options.Model))
        {
            if (options.Tools?.Any() == true)
                throw new NotSupportedException("You.com research models do not support tool definitions. Use agent models for grounded agent behavior.");

            var result = await ExecuteResearchAsync(options.Model, prompt, options.ResponseFormat, null, cancellationToken);
            foreach (var chunk in ChunkText(result.Text))
            {
                yield return new ChatCompletionUpdate
                {
                    Id = responseId,
                    Created = createdAt,
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
                Id = responseId,
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
                ],
                Usage = BuildChatUsage()
            };

            yield break;
        }

        if (!IsAgentModel(options.Model))
            throw new NotSupportedException($"Unsupported You.com chat model '{options.Model}'.");

        var finishReason = "error";
        long? runtimeMs = null;

        await foreach (var evt in StreamAgentEventsAsync(options.Model, prompt, options.ResponseFormat, options.Tools?.Any() == true, new YouComRequestMetadata(), cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(evt.Delta))
            {
                yield return new ChatCompletionUpdate
                {
                    Id = responseId,
                    Created = createdAt,
                    Model = options.Model,
                    Choices =
                    [
                        new
                        {
                            index = 0,
                            delta = new { content = evt.Delta }
                        }
                    ]
                };
            }

            if (evt.Type == "response.done")
            {
                finishReason = evt.Finished == false ? "error" : "stop";
                runtimeMs = evt.RuntimeMs;
            }
        }

        yield return new ChatCompletionUpdate
        {
            Id = responseId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = options.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = finishReason
                }
            ],
            Usage = runtimeMs is null ? BuildChatUsage() : new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0, runtime_ms = runtimeMs.Value }
        };
    }
}
