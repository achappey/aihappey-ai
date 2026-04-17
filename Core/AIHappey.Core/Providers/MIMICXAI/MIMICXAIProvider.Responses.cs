using System.Runtime.CompilerServices;
using AIHappey.Responses.Streaming;
using AIHappey.Responses;
using System.Text;

namespace AIHappey.Core.Providers.MIMICXAI;

public partial class MIMICXAIProvider
{
    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return ExecuteNativeResponseAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return ExecuteNativeResponseStreamingAsync(options, cancellationToken);
    }



    private async Task<ResponseResult> ExecuteNativeResponseAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var request = BuildNativeTextRequest(options);
        var generated = await ExecuteNativeGenerateAsync(request, cancellationToken);
        var itemId = Guid.NewGuid().ToString("n");

        return new ResponseResult
        {
            Id = generated.Id,
            Model = options.Model ?? string.Empty,
            CreatedAt = generated.CreatedAt,
            CompletedAt = generated.CreatedAt,
            Status = "completed",
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = options.Metadata,
            Output = BuildResponseOutput(itemId, generated.Text)
        };
    }

    private async IAsyncEnumerable<ResponseStreamPart> ExecuteNativeResponseStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var request = BuildNativeTextRequest(options);
        var responseId = Guid.NewGuid().ToString("n");
        var itemId = Guid.NewGuid().ToString("n");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sequence = 0;
        var fullText = new StringBuilder();

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = CreateStreamingResponseState(responseId, options, createdAt)
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequence++,
            Response = CreateStreamingResponseState(responseId, options, createdAt)
        };

        await foreach (var delta in ExecuteNativeStreamTextAsync(request, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(delta))
                continue;

            fullText.Append(delta);

            yield return new ResponseOutputTextDelta
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                Outputindex = 0,
                ContentIndex = 0,
                Delta = delta
            };
        }

        var finalText = fullText.ToString();

        yield return new ResponseOutputTextDone
        {
            SequenceNumber = sequence++,
            ItemId = itemId,
            Outputindex = 0,
            ContentIndex = 0,
            Text = finalText
        };

        yield return new ResponseCompleted
        {
            SequenceNumber = sequence++,
            Response = new ResponseResult
            {
                Id = responseId,
                Model = options.Model ?? string.Empty,
                CreatedAt = createdAt,
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Status = "completed",
                Temperature = options.Temperature,
                MaxOutputTokens = options.MaxOutputTokens,
                Metadata = options.Metadata,
                Output = BuildResponseOutput(itemId, finalText)
            }
        };
    }

}
