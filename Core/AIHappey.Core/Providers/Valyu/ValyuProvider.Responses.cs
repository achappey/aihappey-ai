using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Valyu;

public partial class ValyuProvider
{
    private async Task<ResponseResult> CompleteValyuResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        var model = options?.Model ?? throw new ArgumentException("Model missing", nameof(options));

        if (IsAnswerModel(model))
            return await CompleteValyuAnswerResponsesAsync(options, cancellationToken);

        if (IsDeepResearchModel(model))
            return await CompleteValyuDeepResearchResponsesAsync(options, cancellationToken);

        throw new NotSupportedException($"Valyu model '{model}' is not supported for responses.");
    }

    private async IAsyncEnumerable<ResponseStreamPart> CompleteValyuResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await CompleteValyuResponsesAsync(options, cancellationToken);
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

    private async Task<ResponseResult> CompleteValyuAnswerResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        var query = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Valyu answer requires non-empty input for responses.");

        var searchType = ResolveAnswerSearchType(options.Model);
        var passthrough = GetRawProviderPassthroughFromResponseRequest(options);
        var result = await ExecuteAnswerAsync(query, searchType, passthrough, cancellationToken);
        var text = result.Text;

        return new ResponseResult
        {
            Id = Guid.NewGuid().ToString("n"),
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
            Usage = BuildAnswerUsage(result.Metadata),
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

    private async Task<ResponseResult> CompleteValyuDeepResearchResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        var query = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Valyu deep research requires non-empty input for responses.");

        var mode = ResolveDeepResearchMode(options.Model);
        var passthrough = GetRawProviderPassthroughFromResponseRequest(options);
        var result = await ExecuteDeepResearchAsync(query, mode, passthrough, downloadArtifacts: false, cancellationToken);

        return new ResponseResult
        {
            Id = Guid.NewGuid().ToString("n"),
            Object = "response",
            CreatedAt = UnixNow(),
            CompletedAt = UnixNow(),
            Status = result.IsSuccess ? "completed" : "failed",
            Model = options.Model ?? string.Empty,
            Temperature = options.Temperature,
            Metadata = result.Metadata,
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
            Error = result.IsSuccess
                ? null
                : new ResponseResultError
                {
                    Code = "valyu_deepresearch_failed",
                    Message = result.Error ?? "Valyu deep research failed."
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
                            text = result.Text
                        }
                    }
                }
            ]
        };
    }

    private static string ExtractOutputTextFromResponseOutput(IEnumerable<object> output)
    {
        var first = output?.FirstOrDefault();
        if (first is null)
            return string.Empty;

        if (first is System.Text.Json.JsonElement outputEl && outputEl.ValueKind == System.Text.Json.JsonValueKind.Object
            && outputEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var firstContent = contentEl.EnumerateArray().FirstOrDefault();
            if (firstContent.ValueKind == System.Text.Json.JsonValueKind.Object
                && firstContent.TryGetProperty("text", out var textEl)
                && textEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return textEl.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }
}

