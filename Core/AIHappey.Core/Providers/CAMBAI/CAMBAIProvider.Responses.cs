using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.CAMBAI;

public partial class CAMBAIProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model ?? throw new ArgumentException(nameof(options.Model));
        var texts = ExtractResponseRequestTexts(options);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = await TranslateTextsFromModelAsync(modelId, texts, cancellationToken);

        var parts = translated
            .Select(t => new
            {
                type = "output_text",
                text = t,
                annotations = Array.Empty<string>()
            })
            .ToArray();

        var now = DateTimeOffset.UtcNow;
        return new ResponseResult
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
                    content = parts
                }
            ]
        };
    }


    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
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

        IReadOnlyList<string>? translated = null;
        string? error = null;

        try
        {
            translated = await TranslateTextsFromModelAsync(modelId, texts, cancellationToken);
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
                Code = "translation_error",
                Message = error!,
                Param = "model"
            };
            yield break;
        }

        for (var i = 0; i < translated!.Count; i++)
        {
            var itemId = Guid.NewGuid().ToString("n");
            var text = translated[i];

            yield return new ResponseOutputTextDelta
            {
                SequenceNumber = seq++,
                ItemId = itemId,
                ContentIndex = i,
                Outputindex = 0,
                Delta = text
            };

            yield return new ResponseOutputTextDone
            {
                SequenceNumber = seq++,
                ItemId = itemId,
                ContentIndex = i,
                Outputindex = 0,
                Text = text
            };
        }

        var parts = translated
            .Select(t => new { type = "output_text", text = t, annotations = Array.Empty<string>() })
            .ToArray();

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
                        id = Guid.NewGuid().ToString("n"),
                        status = "completed",
                        role = "assistant",
                        content = parts
                    }
                ]
            }
        };
    }

}

