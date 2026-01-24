using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.GTranslate;

public partial class GTranslateProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        return await TranslateResponsesAsync(options, cancellationToken);
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var texts = ExtractResponseRequestTexts(options);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var responseId = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.ToUnixTimeSeconds();
        var seq = 0;

        var baseResponse = new ResponseResult
        {
            Id = responseId,
            Model = options.Model ?? "translate",
            CreatedAt = createdAt,
            Output = []
        };

        yield return new ResponseCreated
        {
            SequenceNumber = seq++,
            Response = baseResponse
        };

        ResponseStreamPart? errorPart = null;
        IReadOnlyList<string>? result = null;
        try
        {
            result = await TranslateAsync(texts, options.Model ?? string.Empty, cancellationToken);
        }
        catch (Exception ex)
        {
            errorPart = new ResponseError
            {
                SequenceNumber = seq++,
                Code = "translation_error",
                Message = ex.Message,
                Param = "language"
            };
        }

        if (errorPart is not null)
        {
            yield return errorPart;
            yield break;
        }

        var itemId = Guid.NewGuid().ToString("n");
        var full = new List<string>();

        for (var i = 0; i < result!.Count; i++)
        {
            var t = result[i];
            full.Add(t);
            var piece = (i == result.Count - 1) ? t : (t + "\n");

            yield return new ResponseOutputTextDelta
            {
                SequenceNumber = seq++,
                ItemId = itemId,
                ContentIndex = 0,
                Outputindex = 0,
                Delta = piece
            };
        }

        var joined = string.Join("\n", full);

        yield return new ResponseOutputTextDone
        {
            SequenceNumber = seq++,
            ItemId = itemId,
            ContentIndex = 0,
            Outputindex = 0,
            Text = joined
        };

        yield return new ResponseCompleted
        {
            SequenceNumber = seq++,
            Response = new ResponseResult
            {
                Id = responseId,
                Model = options.Model ?? "translate",
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
                            new { type = "output_text", text = joined, annotations = Array.Empty<string>() }
                        }
                    }
                ]
            }
        };
    }
}
