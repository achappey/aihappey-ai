using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.BrowserUse;

public partial class BrowserUseProvider
{
    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return ExecuteResponsesAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return ExecuteResponsesStreamingAsync(options, cancellationToken);
    }

    private async Task<ResponseResult> ExecuteResponsesAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("BrowserUse requires non-empty input.");

        var terminal = await ExecuteNativeTaskAsync(new BrowserUseCreateTaskRequest
        {
            Task = prompt,
            Llm = options.Model,
            MaxSteps = options.MaxOutputTokens ?? 100,
            StructuredOutput = TryExtractStructuredOutputSchemaString(options.Text)
        }, cancellationToken);

        return ToResponseResult(terminal, options);
    }

    private async IAsyncEnumerable<ResponseStreamPart> ExecuteResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("BrowserUse requires non-empty input.");

        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var responseId = Guid.NewGuid().ToString("n");
        var itemId = $"msg_{responseId}";
        var sequence = 1;

        var inProgressResponse = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            Status = "in_progress",
            Model = options.Model!,
            Temperature = options.Temperature,
            Metadata = options.Metadata,
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Text = options.Text,
            ParallelToolCalls = options.ParallelToolCalls
        };

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = inProgressResponse
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequence++,
            Response = inProgressResponse
        };

        await foreach (var evt in StreamNativeTaskAsync(new BrowserUseCreateTaskRequest
                       {
                           Task = prompt,
                           Llm = options.Model,
                           MaxSteps = options.MaxOutputTokens ?? 100,
                           StructuredOutput = TryExtractStructuredOutputSchemaString(options.Text)
                       }, cancellationToken))
        {
            switch (evt)
            {
                case BrowserUseNativeCreatedStreamEvent created:
                    responseId = created.Created.Id;
                    itemId = $"msg_{responseId}";
                    break;

                case BrowserUseNativeActionStreamEvent actionEvent:
                {
                    var delta = FormatActionDelta(actionEvent.Action);
                    if (string.IsNullOrWhiteSpace(delta))
                        break;

                    yield return new ResponseOutputTextDelta
                    {
                        SequenceNumber = sequence++,
                        ItemId = itemId,
                        Outputindex = 0,
                        ContentIndex = 0,
                        Delta = delta
                    };
                    break;
                }

                case BrowserUseNativeTerminalStreamEvent terminalEvent:
                {
                    var finalText = terminalEvent.Terminal.OutputText;
                    yield return new ResponseOutputTextDone
                    {
                        SequenceNumber = sequence++,
                        ItemId = itemId,
                        Outputindex = 0,
                        ContentIndex = 0,
                        Text = finalText
                    };

                    var finalResult = ToResponseResult(terminalEvent.Terminal, options);

                    if (IsFinished(terminalEvent.Terminal.Status.Status))
                    {
                        yield return new ResponseCompleted
                        {
                            SequenceNumber = sequence,
                            Response = finalResult
                        };
                    }
                    else
                    {
                        yield return new ResponseFailed
                        {
                            SequenceNumber = sequence,
                            Response = finalResult
                        };
                    }

                    yield break;
                }
            }
        }
    }

    private static string FormatActionDelta(BrowserUseNativeActionEvent action)
    {
        if (action.IsDone)
            return $"Step {action.StepNumber}: done.\n";

        return $"Step {action.StepNumber}: {action.ToolName}.\n";
    }

    private ResponseResult ToResponseResult(BrowserUseNativeTerminalResult terminal, ResponseRequest request)
    {
        var task = terminal.Task;
        var status = terminal.Status;
        var text = terminal.OutputText;
        var createdAt = ToUnixTime(task.CreatedAt);
        var completedAt = ParseUnixTimeOrNow(status.FinishedAt);
        var isCompleted = IsFinished(status.Status);

        return new ResponseResult
        {
            Id = task.Id,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            Status = isCompleted ? "completed" : "failed",
            Model = request.Model ?? task.Llm,
            Temperature = request.Temperature,
            Metadata = MergeMetadata(request.Metadata, task, status),
            MaxOutputTokens = request.MaxOutputTokens,
            Store = request.Store,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>() ?? [],
            Text = request.Text,
            ParallelToolCalls = request.ParallelToolCalls,
            Usage = new
            {
                cost = status.Cost,
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0
            },
            Error = isCompleted
                ? null
                : new ResponseResultError
                {
                    Code = "browseruse_task_stopped",
                    Message = string.IsNullOrWhiteSpace(status.Output)
                        ? "BrowserUse task stopped before completion."
                        : status.Output
                },
            Output =
            [
                new
                {
                    id = $"msg_{task.Id}",
                    type = "message",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text
                        }
                    }
                }
            ]
        };
    }

    private static Dictionary<string, object?> MergeMetadata(
        Dictionary<string, object?>? current,
        BrowserUseTaskView task,
        BrowserUseTaskStatusView status)
    {
        var merged = current is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(current);

        merged["browseruse_task_id"] = task.Id;
        merged["browseruse_session_id"] = task.SessionId;
        merged["browseruse_status"] = status.Status;
        merged["browseruse_is_success"] = status.IsSuccess;
        merged["browseruse_cost"] = status.Cost;

        return merged;
    }
}

