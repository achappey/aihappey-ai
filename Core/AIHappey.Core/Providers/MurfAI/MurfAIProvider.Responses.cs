using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Responses.Streaming;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.MurfAI;

public sealed partial class MurfAIProvider 
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(options);
        var model = await this.GetModel(options.Model, cancellationToken: cancellationToken);
        if (model.Type == "language")
        {
            var now = DateTimeOffset.UtcNow;

            var texts = ExtractResponseRequestTexts(options);
            if (texts.Count == 0)
                throw new Exception("No prompt provided.");

            var result = await TranslateAsync(texts, options.Model?.Split("/").Last()!, cancellationToken);
            var joined = string.Join("\n", result.Translations.Select(t => t.TranslatedText));

            return new ResponseResult
            {
                Id = Guid.NewGuid().ToString("n"),
                Model = options.Model ?? "translate",
                CreatedAt = now.ToUnixTimeSeconds(),
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
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
                                text = joined,
                                annotations = Array.Empty<string>()
                            }
                        }
                    }
                ]
            };
        }

        return await this.SpeechResponseAsync(options, cancellationToken);
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

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
        TranslationResult? result = null;
        try
        {
            result = await TranslateAsync(texts, options.Model?.Split("/").Last()!, cancellationToken);
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

        for (var i = 0; i < result!.Translations.Count; i++)
        {
            var t = result.Translations[i].TranslatedText;
            full.Add(t);
            var piece = (i == result.Translations.Count - 1) ? t : (t + "\n");

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

