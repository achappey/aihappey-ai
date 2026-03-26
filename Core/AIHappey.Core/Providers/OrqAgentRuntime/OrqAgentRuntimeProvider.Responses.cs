using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.OrqAgentRuntime;

public partial class OrqAgentRuntimeProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        ApplyAuthHeader();

        var response = await InvokeInternalAsync(BuildInvokeRequest(options, stream: false), cancellationToken);
        return ToResponseResult(options, response);
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        ApplyAuthHeader();

        var responseId = Guid.NewGuid().ToString("n");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var textState = new Dictionary<string, string>(StringComparer.Ordinal);
        var accumulatedText = new System.Text.StringBuilder();
        OrqInvokeResponse? finalChunk = null;
        object? usage = null;
        int sequence = 1;
        string? failureMessage = null;

        var initial = CreateInProgressResponse(options, responseId, createdAt);

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = initial
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequence++,
            Response = initial
        };


        await foreach (var chunk in InvokeStreamingInternalAsync(BuildInvokeRequest(options, stream: true), cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(chunk.Id))
                responseId = chunk.Id!;

            if (TryConvertJsonElement(chunk.Usage, out var usageObject))
                usage = usageObject;

            foreach (var choice in chunk.Choices ?? [])
            {
                var delta = GetIncrementalDelta(textState, $"choice:{choice.Index}:text", ExtractMessageText(choice.Message));
                if (string.IsNullOrEmpty(delta))
                    continue;

                accumulatedText.Append(delta);
                yield return new ResponseOutputTextDelta
                {
                    SequenceNumber = sequence++,
                    ItemId = $"msg_{responseId}",
                    ContentIndex = 0,
                    Outputindex = 0,
                    Delta = delta
                };
            }

            if (chunk.IsFinal)
                finalChunk = chunk;
        }


        var finalText = finalChunk is null
            ? accumulatedText.ToString()
            : ExtractPrimaryOutputText(finalChunk, accumulatedText.ToString());

        if (!string.IsNullOrEmpty(finalText))
        {
            yield return new ResponseOutputTextDone
            {
                SequenceNumber = sequence++,
                ItemId = $"msg_{responseId}",
                ContentIndex = 0,
                Outputindex = 0,
                Text = finalText
            };
        }

        var finalResponse = finalChunk is null
            ? BuildSyntheticResponseResult(options, responseId, createdAt, finalText, usage, failureMessage)
            : ToResponseResult(options, finalChunk, overrideCreatedAt: createdAt, fallbackText: finalText, overrideUsage: usage, failureMessage: failureMessage);

        if (string.Equals(finalResponse.Status, "completed", StringComparison.OrdinalIgnoreCase))
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
    }
}
