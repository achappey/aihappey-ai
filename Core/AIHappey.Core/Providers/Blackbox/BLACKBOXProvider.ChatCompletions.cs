using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;

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
                new ChatCompletionChoice { Index = 0, Message = new ChatCompletionResponseMessage { Role = "assistant", Content = terminal.OutputText }, FinishReason = finishReason }
            ],
            Usage = new ChatCompletionUsage { PromptTokens = 0, CompletionTokens = 0, TotalTokens = 0 }
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
                                new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta { Content = message } }
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
                                new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta {  }, FinishReason = GetStatusFinishReason(terminalStatus) }
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
                                new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta {  }, FinishReason = "error" }
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
                new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta {  }, FinishReason = GetStatusFinishReason(terminalStatus) }
            ]
        };
    }
}
