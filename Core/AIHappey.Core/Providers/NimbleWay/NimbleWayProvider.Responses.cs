using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.NimbleWay;

public partial class NimbleWayProvider
{
    private async Task<ResponseResult> CompleteNimbleWayResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        var query = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("NimbleWay requires non-empty input for responses.");

        var passthrough = GetRawProviderPassthroughFromResponseRequest(options);
        var result = await ExecuteNimbleWayAsync(options.Model, query, passthrough, cancellationToken);
        var parts = BuildOrderedTextParts(result)
            .Select(text => new { type = "output_text", text, annotations = Array.Empty<string>() })
            .ToArray();

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
                total_tokens = 0
            },
            Output =
            [
                new
                {
                    id = $"msg_{Guid.NewGuid():n}",
                    type = "message",
                    role = "assistant",
                    content = parts
                }
            ]
        };
    }

    private async IAsyncEnumerable<ResponseStreamPart> CompleteNimbleWayResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await CompleteNimbleWayResponsesAsync(options, cancellationToken);
        var responseId = result.Id;
        var itemId = $"msg_{responseId}";
        var segments = ExtractOutputTextPartsFromResponseOutput(result.Output).ToList();

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

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            yield return new ResponseOutputTextDelta
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                Outputindex = 0,
                ContentIndex = i,
                Delta = segment
            };

            yield return new ResponseOutputTextDone
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                Outputindex = 0,
                ContentIndex = i,
                Text = segment
            };
        }

        yield return new ResponseCompleted
        {
            SequenceNumber = sequence,
            Response = result
        };
    }

    private static IEnumerable<string> ExtractOutputTextPartsFromResponseOutput(IEnumerable<object> output)
    {
        var first = output?.FirstOrDefault();
        if (first is not System.Text.Json.JsonElement outputEl || outputEl.ValueKind != System.Text.Json.JsonValueKind.Object)
            return [];

        if (!outputEl.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];

        var parts = new List<string>();
        foreach (var content in contentEl.EnumerateArray())
        {
            if (content.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;

            if (content.TryGetProperty("text", out var textEl) && textEl.ValueKind == System.Text.Json.JsonValueKind.String)
                parts.Add(textEl.GetString() ?? string.Empty);
        }

        return parts;
    }
}

