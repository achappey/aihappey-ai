using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Inferencesh;

public partial class InferenceshProvider
{
    private async Task<ResponseResult> ExecuteResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var model = options.Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(options));

        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Inference.sh requires non-empty input.");

        var task = await RunTaskAsync(
            ResolveInferenceAppId(model),
            prompt,
            options.Temperature,
            options.MaxOutputTokens,
            options.TopP is null ? null : (float?)options.TopP,
            cancellationToken);

        return BuildResponseResultFromTask(task, options);
    }

    private async IAsyncEnumerable<ResponseStreamPart> ExecuteResponsesStreamingAsync(
        ResponseRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var model = options.Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(options));

        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Inference.sh requires non-empty input.");

        var runTask = await RunTaskAsync(
            ResolveInferenceAppId(model),
            prompt,
            options.Temperature,
            options.MaxOutputTokens,
            options.TopP is null ? null : (float?)options.TopP,
            cancellationToken,
            waitForTerminal: false);

        var responseId = runTask.Id;
        var itemId = $"msg_{responseId}";
        var createdAt = ToUnixTimeOrNow(runTask.CreatedAt);
        var sequence = 1;

        var inProgress = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            Status = "in_progress",
            Model = model,
            Temperature = options.Temperature,
            Metadata = options.Metadata,
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Text = options.Text,
            ParallelToolCalls = options.ParallelToolCalls,
            Usage = null,
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

        await foreach (var update in StreamTaskTextUpdatesAsync(runTask.Id, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(update.Delta))
            {
                yield return new ResponseOutputTextDelta
                {
                    SequenceNumber = sequence++,
                    ItemId = itemId,
                    Outputindex = 0,
                    ContentIndex = 0,
                    Delta = update.Delta
                };
            }

            if (!update.IsTerminal)
                continue;

            yield return new ResponseOutputTextDone
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                Outputindex = 0,
                ContentIndex = 0,
                Text = update.FullText
            };

            var result = BuildResponseResultFromTask(update.Task, options);
            if (update.IsSuccess)
            {
                yield return new ResponseCompleted
                {
                    SequenceNumber = sequence,
                    Response = result
                };
            }
            else
            {
                yield return new ResponseFailed
                {
                    SequenceNumber = sequence,
                    Response = result
                };
            }

            yield break;
        }
    }

    private static ResponseResult BuildResponseResultFromTask(InferenceTask task, ResponseRequest request)
    {
        var text = ExtractTaskText(task);
        var isSuccess = IsSuccessStatus(task.Status);
        var createdAt = ToUnixTimeOrNow(task.CreatedAt);
        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new ResponseResult
        {
            Id = task.Id,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            Status = isSuccess ? "completed" : "failed",
            Model = request.Model!,
            Temperature = request.Temperature,
            Metadata = request.Metadata,
            MaxOutputTokens = request.MaxOutputTokens,
            Store = request.Store,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>() ?? [],
            Text = request.Text,
            ParallelToolCalls = request.ParallelToolCalls,
            Usage = TryBuildUsage(task.Output),
            Error = isSuccess
                ? null
                : new ResponseResultError
                {
                    Code = "inferencesh_task_failed",
                    Message = task.Error ?? "Inference.sh task failed."
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
}

