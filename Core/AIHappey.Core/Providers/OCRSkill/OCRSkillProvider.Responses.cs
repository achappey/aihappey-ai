using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.OCRSkill;

public sealed partial class OCRSkillProvider
{
    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return CompleteResponsesViaChatCompletionsAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return CompleteResponsesStreamingViaChatCompletionsAsync(options, cancellationToken);
    }

    private async Task<ResponseResult> CompleteResponsesViaChatCompletionsAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        var completionOptions = options.ToChatCompletionOptions();
        var completion = await CompleteChatAsync(completionOptions, cancellationToken);
        return completion.ToResponseResult(options);
    }

    private async IAsyncEnumerable<ResponseStreamPart> CompleteResponsesStreamingViaChatCompletionsAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var completionOptions = options.ToChatCompletionOptions();
        var responseId = $"resp_{Guid.NewGuid():n}";
        var itemId = $"msg_{responseId}";
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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

        await foreach (var update in CompleteChatStreamingAsync(completionOptions, cancellationToken))
        {
            var updateId = update.Id ?? responseId;
            responseId = updateId;
            itemId = $"msg_{updateId}";

            var delta = OCRSkillProviderMapping.TryGetDeltaText(update);
            if (!string.IsNullOrWhiteSpace(delta))
            {
                yield return new ResponseOutputTextDelta
                {
                    SequenceNumber = sequence++,
                    ItemId = itemId,
                    Outputindex = 0,
                    ContentIndex = 0,
                    Delta = delta
                };
            }

            var finishReason = OCRSkillProviderMapping.TryGetFinishReason(update);
            if (string.IsNullOrWhiteSpace(finishReason))
                continue;

            var finalResponse = update.ToResponseResult(options, responseId, itemId, createdAt, finishReason);

            yield return new ResponseOutputTextDone
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                Outputindex = 0,
                ContentIndex = 0,
                Text = OCRSkillProviderMapping.ExtractAssistantText(finalResponse.Output)
            };

            if (string.Equals(finishReason, "stop", StringComparison.OrdinalIgnoreCase))
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
    }
}
