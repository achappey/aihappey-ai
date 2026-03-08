using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.JigsawStack;

public partial class JigsawStackProvider
{
    public async Task<ResponseResult> ResponsesAsync(
   ResponseRequest options,
   CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model ?? throw new ArgumentException(nameof(options.Model));
        var texts = ExtractResponseRequestTexts(options);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var executed = await ExecuteModelAsync(modelId, texts, metadata: null, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        return BuildResponseResult(modelId, executed.Text, now);
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var texts = ExtractResponseRequestTexts(options);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var responseId = Guid.NewGuid().ToString("n");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var seq = 0;

        yield return new ResponseCreated
        {
            SequenceNumber = seq++,
            Response = new ResponseResult
            {
                Id = responseId,
                Model = modelId,
                CreatedAt = createdAt,
                Output = []
            }
        };

        JigsawExecutionResult? executed = null;
        string? error = null;

        try
        {
            executed = await ExecuteModelAsync(modelId, texts, metadata: null, cancellationToken);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            yield return new ResponseError
            {
                SequenceNumber = seq++,
                Code = "jigsawstack_error",
                Message = error!,
                Param = "model"
            };
            yield break;
        }

        var itemId = Guid.NewGuid().ToString("n");
        yield return new ResponseOutputTextDelta
        {
            SequenceNumber = seq++,
            ItemId = itemId,
            ContentIndex = 0,
            Outputindex = 0,
            Delta = executed!.Text
        };

        yield return new ResponseOutputTextDone
        {
            SequenceNumber = seq++,
            ItemId = itemId,
            ContentIndex = 0,
            Outputindex = 0,
            Text = executed.Text
        };

        yield return new ResponseCompleted
        {
            SequenceNumber = seq++,
            Response = new ResponseResult
            {
                Id = responseId,
                Model = modelId,
                CreatedAt = createdAt,
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
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
                                text = executed.Text,
                                annotations = Array.Empty<string>()
                            }
                        }
                    }
                ]
            }
        };
    }

    private static ResponseResult BuildResponseResult(string modelId, string text, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid().ToString("n"),
            Model = modelId,
            CreatedAt = now.ToUnixTimeSeconds(),
            CompletedAt = now.ToUnixTimeSeconds(),
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
                            text,
                            annotations = Array.Empty<string>()
                        }
                    }
                }
            ]
        };
}
