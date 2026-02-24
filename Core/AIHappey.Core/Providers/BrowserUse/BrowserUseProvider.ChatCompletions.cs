using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Extensions;
using AIHappey.Responses;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.BrowserUse;

public partial class BrowserUseProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await CompleteChatViaResponsesAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return CompleteChatStreamingViaResponsesAsync(options, cancellationToken);
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingViaResponsesAsync(
       ChatCompletionOptions options,
       [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ResponseRequest
        {
            Model = options.Model,
            Temperature = options.Temperature,
            Text = options.ResponseFormat,
            Input = BuildPromptFromCompletionMessages(options.Messages),
            Stream = true
        };

        await foreach (var part in ExecuteResponsesStreamingAsync(request, cancellationToken))
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


    private async Task<ChatCompletion> CompleteChatViaResponsesAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        var request = new ResponseRequest
        {
            Model = options.Model,
            Temperature = options.Temperature,
            MaxOutputTokens = null,
            Text = options.ResponseFormat,
            Input = BuildPromptFromCompletionMessages(options.Messages)
        };

        var response = await ExecuteResponsesAsync(request, cancellationToken);
        var text = ExtractOutputText(response.Output);

        return new ChatCompletion
        {
            Id = response.Id,
            Object = "chat.completion",
            Created = response.CreatedAt,
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
                    finish_reason = response.Status == "completed" ? "stop" : "error"
                }
            ],
            Usage = response.Usage
        };
    }


}
