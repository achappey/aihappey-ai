using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BLACKBOX;

public partial class BLACKBOXProvider
{
    private async Task<ChatCompletion> CompleteNativeAgentChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var prompt = BuildNativeTaskPromptFromCompletionMessages(options.Messages);
        var terminal = await ExecuteNativeAgentTaskAsync(options.Model, prompt, cancellationToken);
        var finishReason = GetStatusFinishReason(terminal.Status.Status);

        return new ChatCompletion
        {
            Id = terminal.Task.Id,
            Object = "chat.completion",
            Created = ToUnixTimeOrNow(terminal.Task.CreatedAt),
            Model = options.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = terminal.OutputText
                    },
                    finish_reason = finishReason
                }
            ],
            Usage = new
            {
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0
            }
        };
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteNativeAgentChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!TryResolveNativeAgent(options.Model, out var selectedAgent, out var selectedModel))
            throw new NotSupportedException(BuildUnsupportedNativeAgentModelMessage(options.Model));

        var prompt = BuildNativeTaskPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("BLACKBOX native agent requires a non-empty prompt.");

        var created = await CreateNativeTaskAsync(new BlackboxNativeTaskCreateRequest
        {
            Prompt = prompt,
            SelectedAgent = selectedAgent,
            SelectedModel = selectedModel
        }, cancellationToken);

        var completionId = created.TaskId;
        var terminalStatus = "error";


        await foreach (var evt in StreamNativeTaskEventsAsync(created.TaskId, fromIndex: 0, includeStatus: true, cancellationToken))
        {
            switch (evt.EventType)
            {
                case "log" when evt.Data is JsonElement logData && TryExtractLogPayload(logData, out _, out var message, out _, out _, out _, out _):
                    {
                        if (string.IsNullOrWhiteSpace(message))
                            break;

                        yield return new ChatCompletionUpdate
                        {
                            Id = completionId,
                            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            Model = options.Model,
                            Choices =
                            [
                                new
                                {
                                    index = 0,
                                    delta = new { content = message }
                                }
                            ]
                        };
                        break;
                    }

                case "status" when evt.Data is JsonElement statusData && TryExtractStatusPayload(statusData, out var statusValue, out _):
                    terminalStatus = statusValue;
                    break;

                case "complete" when evt.Data is JsonElement completeData:
                    {
                        if (TryExtractStatusPayload(completeData, out var completeStatus, out _))
                            terminalStatus = completeStatus;

                        yield return new ChatCompletionUpdate
                        {
                            Id = completionId,
                            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            Model = options.Model,
                            Choices =
                            [
                                new
                                {
                                    index = 0,
                                    delta = new { },
                                    finish_reason = GetStatusFinishReason(terminalStatus)
                                }
                            ]
                        };

                        yield break;
                    }

                case "error":
                    {
                        terminalStatus = "error";
                        yield return new ChatCompletionUpdate
                        {
                            Id = completionId,
                            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            Model = options.Model,
                            Choices =
                            [
                                new
                                {
                                    index = 0,
                                    delta = new { },
                                    finish_reason = "error"
                                }
                            ]
                        };

                        yield break;
                    }
            }
        }


        yield return new ChatCompletionUpdate
        {
            Id = completionId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = options.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = GetStatusFinishReason(terminalStatus)
                }
            ]
        };
    }
}
