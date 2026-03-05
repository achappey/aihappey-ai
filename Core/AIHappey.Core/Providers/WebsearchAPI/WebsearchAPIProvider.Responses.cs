using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.WebsearchAPI;

public partial class WebsearchAPIProvider
{
    private async Task<ResponseResult> CompleteWebSearchResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        var query = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("WebsearchAPI requires non-empty input for responses.");

        var passthrough = GetRawProviderPassthroughFromResponseRequest(options);
        var result = await ExecuteAiSearchAsync(query, passthrough, cancellationToken);
        var text = BuildAnswerWithSourceMarkdown(result);

        return new ResponseResult
        {
            Id = Guid.NewGuid().ToString("n"),
            Object = "response",
            CreatedAt = UnixNow(),
            CompletedAt = UnixNow(),
            Status = "completed",
            Model = options.Model ?? string.Empty,
            Temperature = options.Temperature,
            Metadata = MergeMetadata(options.Metadata, BuildResultMetadata(result)),
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Text = options.Text,
            ParallelToolCalls = options.ParallelToolCalls,
            Usage = new
            {
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0,
                response_time = result.ResponseTime
            },
            Output =
            [
                new
                {
                    id = $"msg_{Guid.NewGuid():n}",
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

    private async IAsyncEnumerable<ResponseStreamPart> CompleteWebSearchResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await CompleteWebSearchResponsesAsync(options, cancellationToken);
        var responseId = result.Id;
        var text = ExtractOutputTextFromResponseOutput(result.Output);
        var itemId = $"msg_{responseId}";

        var sequence = 1;
        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = new ResponseResult
            {
                Id = responseId,
                Object = "response",
                CreatedAt = result.CreatedAt,
                Status = "in_progress",
                Model = result.Model,
                Temperature = result.Temperature,
                Metadata = result.Metadata,
                MaxOutputTokens = result.MaxOutputTokens,
                Store = result.Store,
                ToolChoice = result.ToolChoice,
                Tools = result.Tools,
                Text = result.Text,
                ParallelToolCalls = result.ParallelToolCalls
            }
        };

        yield return new ResponseOutputTextDelta
        {
            SequenceNumber = sequence++,
            ItemId = itemId,
            Outputindex = 0,
            ContentIndex = 0,
            Delta = text
        };

        yield return new ResponseOutputTextDone
        {
            SequenceNumber = sequence++,
            ItemId = itemId,
            Outputindex = 0,
            ContentIndex = 0,
            Text = text
        };

        yield return new ResponseCompleted
        {
            SequenceNumber = sequence,
            Response = result
        };
    }
}

