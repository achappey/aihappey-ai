using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Swarms;

public partial class SwarmsProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Swarms requires non-empty response input.");

        var executed = await ExecuteCompletionAsync(
            options.Model ?? throw new ArgumentException(nameof(options.Model)),
            prompt,
            BuildHistoryFromResponseRequest(options),
            ExtractSystemPrompt(options),
            options.Temperature,
            options.MaxOutputTokens,
            cancellationToken);

        return ToResponseResult(executed, options);
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Swarms requires non-empty response input.");

        var responseId = Guid.NewGuid().ToString("n");
        var itemId = $"msg_{responseId}";
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sequence = 1;

        var inProgress = new ResponseResult
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
            ParallelToolCalls = options.ParallelToolCalls,
            Output = []
        };

        yield return new ResponseCreated { SequenceNumber = sequence++, Response = inProgress };
        yield return new ResponseInProgress { SequenceNumber = sequence++, Response = inProgress };

        await foreach (var delta in ExecuteCompletionStreamingAsync(
                           options.Model!,
                           prompt,
                           BuildHistoryFromResponseRequest(options),
                           ExtractSystemPrompt(options),
                           options.Temperature,
                           options.MaxOutputTokens,
                           cancellationToken))
        {
            yield return new ResponseOutputTextDelta
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                Outputindex = 0,
                ContentIndex = 0,
                Delta = delta
            };

            yield return new ResponseOutputTextDone
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                Outputindex = 0,
                ContentIndex = 0,
                Text = delta
            };

            var executed = await ExecuteCompletionAsync(
                options.Model!,
                prompt,
                BuildHistoryFromResponseRequest(options),
                ExtractSystemPrompt(options),
                options.Temperature,
                options.MaxOutputTokens,
                cancellationToken);

            yield return new ResponseCompleted
            {
                SequenceNumber = sequence,
                Response = ToResponseResult(executed, options)
            };

            yield break;
        }
    }
}
