using System.Runtime.CompilerServices;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.Inferencesh;

public partial class InferenceshProvider
{
    private async Task<ChatCompletion> CompleteChatViaTasksAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var app = ResolveInferenceAppId(options.Model);
        var prompt = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Inference.sh requires non-empty input.");

        var task = await RunTaskAsync(
            app,
            prompt,
            options.Temperature,
            maxOutputTokens: null,
            topP: null,
            cancellationToken: cancellationToken);

        var text = ExtractTaskText(task);
        var usage = TryBuildUsage(task.Output);
        var created = ToUnixTimeOrNow(task.CreatedAt);

        return new ChatCompletion
        {
            Id = task.Id,
            Created = created,
            Model = options.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    finish_reason = IsSuccessStatus(task.Status) ? "stop" : "error",
                    message = new
                    {
                        role = "assistant",
                        content = text
                    }
                }
            ],
            Usage = usage
        };
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingViaTasksAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var app = ResolveInferenceAppId(options.Model);
        var prompt = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Inference.sh requires non-empty input.");

        var runTask = await RunTaskAsync(
            app,
            prompt,
            options.Temperature,
            maxOutputTokens: null,
            topP: null,
            cancellationToken: cancellationToken,
            waitForTerminal: false);

        await foreach (var update in StreamTaskTextUpdatesAsync(runTask.Id, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(update.Delta))
            {
                yield return new ChatCompletionUpdate
                {
                    Id = update.Task.Id,
                    Created = ToUnixTimeOrNow(update.Task.CreatedAt),
                    Model = options.Model,
                    Choices =
                    [
                        new
                        {
                            index = 0,
                            delta = new { content = update.Delta }
                        }
                    ]
                };
            }

            if (!update.IsTerminal)
                continue;

            yield return new ChatCompletionUpdate
            {
                Id = update.Task.Id,
                Created = ToUnixTimeOrNow(update.Task.CreatedAt),
                Model = options.Model,
                Choices =
                [
                    new
                    {
                        index = 0,
                        delta = new { },
                        finish_reason = update.IsSuccess ? "stop" : "error"
                    }
                ],
                Usage = TryBuildUsage(update.Task.Output)
            };

            yield break;
        }
    }
}

