using System.Runtime.CompilerServices;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.LLMLayer;

public partial class LLMLayerProvider
{
    private async Task<ChatCompletion> CompleteChatInternalAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var prompt = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("LLMLayer requires non-empty input derived from chat messages.");

        var payload = BuildAnswerPayload(
            query: prompt,
            model: options.Model,
            temperature: options.Temperature,
            maxTokens: null,
            llmlayerMetadata: null);

        var answer = await ExecuteAnswerAsync(payload, cancellationToken);
        var text = AnswerToText(answer.Answer);
        var usage = BuildUsage(answer);
        var id = Guid.NewGuid().ToString("n");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new ChatCompletion
        {
            Id = id,
            Object = "chat.completion",
            Created = created,
            Model = options.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = text
                    },
                    finish_reason = "stop"
                }
            ],
            Usage = usage
        };
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingInternalAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var request = new ResponseRequest
        {
            Model = options.Model,
            Temperature = options.Temperature,
            Text = options.ResponseFormat,
            Input = BuildPromptFromCompletionMessages(options.Messages),
            Stream = true,
            Metadata = null
        };

        await foreach (var part in ResponsesStreamingInternalAsync(request, cancellationToken))
        {
            switch (part)
            {
                case ResponseOutputTextDelta delta:
                    yield return new ChatCompletionUpdate
                    {
                        Id = delta.ItemId,
                        Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Model = options.Model,
                        Choices =
                        [
                            new
                            {
                                index = 0,
                                delta = new { content = delta.Delta }
                            }
                        ]
                    };
                    break;

                case ResponseCompleted completed:
                    yield return new ChatCompletionUpdate
                    {
                        Id = completed.Response.Id,
                        Created = completed.Response.CompletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
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
                        Usage = completed.Response.Usage
                    };
                    break;

                case ResponseFailed failed:
                    yield return new ChatCompletionUpdate
                    {
                        Id = failed.Response.Id,
                        Created = failed.Response.CompletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Model = options.Model,
                        Choices =
                        [
                            new
                            {
                                index = 0,
                                delta = new { },
                                finish_reason = "error"
                            }
                        ],
                        Usage = failed.Response.Usage
                    };
                    break;
            }
        }
    }
}

