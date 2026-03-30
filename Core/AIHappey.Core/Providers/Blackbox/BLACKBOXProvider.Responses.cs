using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.BLACKBOX;

public partial class BLACKBOXProvider
{


    private async Task<ResponseResult> ExecuteNativeAgentResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        var prompt = BuildNativeTaskPromptFromResponseRequest(options);
        var terminal = await ExecuteNativeAgentTaskAsync(options.Model ?? string.Empty, prompt, cancellationToken);
        return ToNativeResponseResult(terminal, options);
    }

    private async IAsyncEnumerable<ResponseStreamPart> ExecuteNativeAgentResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!TryResolveNativeAgent(options.Model, out var selectedAgent, out var selectedModel))
            throw new NotSupportedException(BuildUnsupportedNativeAgentModelMessage(options.Model));

        var prompt = BuildNativeTaskPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("BLACKBOX native agent requires a non-empty prompt.");

        var created = await CreateNativeTaskAsync(new BlackboxNativeTaskCreateRequest
        {
            Prompt = prompt,
            SelectedAgent = selectedAgent,
            SelectedModel = selectedModel
        }, cancellationToken);

        var taskView = await GetNativeTaskAsync(created.TaskId, cancellationToken);
        var sequence = 1;
        var fullText = new StringBuilder();
        var outputItemId = $"msg_{created.TaskId}";
        var terminalStatus = "error";
        var terminalError = string.Empty;

        var inProgress = new ResponseResult
        {
            Id = created.TaskId,
            Object = "response",
            CreatedAt = ToUnixTimeOrNow(taskView.CreatedAt),
            Status = "in_progress",
            Model = options.Model!,
            Temperature = options.Temperature,
            Metadata = options.Metadata,
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Text = options.Text,
            ParallelToolCalls = options.ParallelToolCalls,
            Output = []
        };

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = inProgress
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequence++,
            Response = inProgress
        };


        await foreach (var evt in StreamNativeTaskEventsAsync(created.TaskId, fromIndex: 0, includeStatus: true, cancellationToken))
        {
            switch (evt.EventType)
            {
                case "log" when evt.Data is JsonElement logData && TryExtractLogPayload(logData, out _, out var message, out _, out _, out _, out _):
                    {
                        if (string.IsNullOrWhiteSpace(message))
                            break;

                        fullText.Append(message);

                        yield return new ResponseOutputTextDelta
                        {
                            SequenceNumber = sequence++,
                            ItemId = outputItemId,
                            Outputindex = 0,
                            ContentIndex = 0,
                            Delta = message
                        };
                        break;
                    }

                case "status" when evt.Data is JsonElement statusData && TryExtractStatusPayload(statusData, out var statusValue, out var statusError):
                    {
                        terminalStatus = statusValue;
                        if (!string.IsNullOrWhiteSpace(statusError))
                            terminalError = statusError!;
                        break;
                    }

                case "complete" when evt.Data is JsonElement completeData:
                    {
                        if (TryExtractStatusPayload(completeData, out var completeStatus, out var completeError))
                        {
                            terminalStatus = completeStatus;
                            if (!string.IsNullOrWhiteSpace(completeError))
                                terminalError = completeError!;
                        }

                        var finalTask = await GetNativeTaskAsync(created.TaskId, cancellationToken);
                        var finalStatus = await GetNativeTaskStatusAsync(created.TaskId, cancellationToken);

                        var finalText = fullText.Length > 0
                            ? fullText.ToString()
                            : ExtractFinalTextFromTask(finalTask);

                        yield return new ResponseOutputTextDone
                        {
                            SequenceNumber = sequence++,
                            ItemId = outputItemId,
                            Outputindex = 0,
                            ContentIndex = 0,
                            Text = finalText
                        };

                        var terminal = new BlackboxNativeTerminalResult
                        {
                            Created = created,
                            Status = finalStatus,
                            Task = finalTask,
                            OutputText = finalText
                        };

                        var finalResponse = ToNativeResponseResult(terminal, options);
                        if (IsCompletedTaskStatus(finalStatus.Status))
                        {
                            yield return new ResponseCompleted
                            {
                                SequenceNumber = sequence,
                                Response = finalResponse
                            };
                        }
                        else
                        {
                            yield return new ResponseFailed
                            {
                                SequenceNumber = sequence,
                                Response = finalResponse
                            };
                        }

                        yield break;
                    }

                case "error":
                    {
                        terminalStatus = "error";
                        if (!string.IsNullOrWhiteSpace(evt.RawData))
                            terminalError = evt.RawData;
                        break;
                    }
            }
        }


        var fallbackTask = await GetNativeTaskAsync(created.TaskId, cancellationToken);
        var fallbackStatus = await GetNativeTaskStatusAsync(created.TaskId, cancellationToken);
        var fallbackText = fullText.Length > 0
            ? fullText.ToString()
            : ExtractFinalTextFromTask(fallbackTask);

        if (string.IsNullOrWhiteSpace(fallbackText) && !string.IsNullOrWhiteSpace(terminalError))
            fallbackText = terminalError;

        yield return new ResponseOutputTextDone
        {
            SequenceNumber = sequence++,
            ItemId = outputItemId,
            Outputindex = 0,
            ContentIndex = 0,
            Text = fallbackText
        };

        var fallbackTerminal = new BlackboxNativeTerminalResult
        {
            Created = created,
            Status = fallbackStatus,
            Task = fallbackTask,
            OutputText = fallbackText
        };

        var fallbackResponse = ToNativeResponseResult(fallbackTerminal, options);
        if (IsCompletedTaskStatus(fallbackStatus.Status))
        {
            yield return new ResponseCompleted
            {
                SequenceNumber = sequence,
                Response = fallbackResponse
            };
        }
        else
        {
            yield return new ResponseFailed
            {
                SequenceNumber = sequence,
                Response = fallbackResponse
            };
        }
    }
}
