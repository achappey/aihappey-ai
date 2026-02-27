using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.ChainGPT;

public partial class ChainGPTProvider
{
    private async Task<ResponseResult> ResponsesInternalAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("No prompt provided.", nameof(options));

        var metadata = TryExtractProviderMetadata(options.Metadata);
        var answer = await CompleteQuestionBufferedAsync(modelId, prompt, metadata, cancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new ResponseResult
        {
            Id = Guid.NewGuid().ToString("n"),
            Model = modelId,
            CreatedAt = now,
            CompletedAt = now,
            Status = "completed",
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = options.Metadata,
            Output =
            [
                new
                {
                    type = "message",
                    id = Guid.NewGuid().ToString("n"),
                    status = "completed",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = answer,
                            annotations = Array.Empty<string>()
                        }
                    }
                }
            ]
        };
    }

    private async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingInternalAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("No prompt provided.", nameof(options));

        var metadata = TryExtractProviderMetadata(options.Metadata);
        var responseId = Guid.NewGuid().ToString("n");
        var itemId = Guid.NewGuid().ToString("n");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sequence = 0;
        var full = string.Empty;

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = new ResponseResult
            {
                Id = responseId,
                Model = modelId,
                CreatedAt = created,
                Temperature = options.Temperature,
                MaxOutputTokens = options.MaxOutputTokens,
                Metadata = options.Metadata,
                Output = []
            }
        };


        await foreach (var chunk in CompleteQuestionStreamingAsync(modelId, prompt, metadata, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(chunk))
                continue;

            full += chunk;

            yield return new ResponseOutputTextDelta
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                ContentIndex = 0,
                Outputindex = 0,
                Delta = chunk
            };
        }


        yield return new ResponseOutputTextDone
        {
            SequenceNumber = sequence++,
            ItemId = itemId,
            ContentIndex = 0,
            Outputindex = 0,
            Text = full
        };

        yield return new ResponseCompleted
        {
            SequenceNumber = sequence++,
            Response = new ResponseResult
            {
                Id = responseId,
                Model = modelId,
                CreatedAt = created,
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Status = "completed",
                Temperature = options.Temperature,
                MaxOutputTokens = options.MaxOutputTokens,
                Metadata = options.Metadata,
                Output =
                [
                    new
                    {
                        type = "message",
                        id = itemId,
                        status = "completed",
                        role = "assistant",
                        content = new[]
                        {
                            new
                            {
                                type = "output_text",
                                text = full,
                                annotations = Array.Empty<string>()
                            }
                        }
                    }
                ]
            }
        };
    }
}
