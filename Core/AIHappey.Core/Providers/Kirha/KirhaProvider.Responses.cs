using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Kirha;

public partial class KirhaProvider
{
    private async Task<ResponseResult> CompleteKirhaResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        var query = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Kirha search requires text from the last user input.");

        var passthrough = GetRawProviderPassthroughFromResponseRequest(options);
        var result = await ExecuteKirhaSearchAsync(options.Model ?? string.Empty, query, passthrough, cancellationToken);

        var output = new List<object>();

        output.AddRange(result.ReasoningItems.Select(r => new
        {
            id = $"rs_{r.Id}",
            type = "reasoning",
            summary = r.Text,
            metadata = r.Metadata
        }));

        output.AddRange(result.ToolCalls.Select(t => new
        {
            id = t.Id,
            type = "tool_call",
            tool_name = t.ToolName,
            input = t.Input,
            output = t.Output,
            provider_executed = t.ProviderExecuted,
            metadata = t.Metadata
        }));

        output.Add(new
        {
            id = $"msg_{Guid.NewGuid():n}",
            type = "message",
            role = "assistant",
            content = new[]
            {
                new
                {
                    type = "output_text",
                    text = result.Summary
                }
            }
        });

        return new ResponseResult
        {
            Id = result.Response.Id ?? Guid.NewGuid().ToString("n"),
            Object = "response",
            CreatedAt = UnixNow(),
            CompletedAt = UnixNow(),
            Status = "completed",
            Model = options.Model ?? string.Empty,
            Temperature = options.Temperature,
            Metadata = result.Metadata,
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Text = options.Text,
            ParallelToolCalls = options.ParallelToolCalls,
            Usage = BuildKirhaUsage(result.Response.Usage),
            Output = output
        };
    }

    private async IAsyncEnumerable<ResponseStreamPart> CompleteKirhaResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await CompleteKirhaResponsesAsync(options, cancellationToken);
        var text = ExtractOutputTextFromResponseOutput(result.Output);
        var itemId = $"msg_{result.Id}";
        var sequence = 1;

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = new ResponseResult
            {
                Id = result.Id,
                Object = result.Object,
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

        if (!string.IsNullOrWhiteSpace(text))
        {
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
        }

        yield return new ResponseCompleted
        {
            SequenceNumber = sequence,
            Response = result
        };
    }
}
