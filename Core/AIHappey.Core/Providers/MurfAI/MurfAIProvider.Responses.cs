using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.MurfAI;

public sealed partial class MurfAIProvider : IModelProvider
{
    public async Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(options);
        var model = await this.GetModel(options.Model, cancellationToken: cancellationToken);
        if (model.Type == "language")
        {
            var texts = ExtractResponseRequestTexts(options);
            if (texts.Count == 0)
                throw new Exception("No prompt provided.");

            var result = await TranslateAsync(texts, options.Model?.Split("/").Last()!, cancellationToken);
            var joined = string.Join("\n", result.Translations.Select(t => t.TranslatedText));

            var now = DateTimeOffset.UtcNow;
            return new Common.Model.Responses.ResponseResult
            {
                Id = Guid.NewGuid().ToString("n"),
                Model = options.Model ?? "translate",
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

    public async IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        Common.Model.Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(options);

        var texts = ExtractResponseRequestTexts(options);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var responseId = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        var createdAt = now.ToUnixTimeSeconds();
        var seq = 0;

        var baseResponse = new Common.Model.Responses.ResponseResult
        {
            Id = responseId,
            Model = options.Model ?? "translate",
            CreatedAt = createdAt,
            Output = []
        };

        yield return new Common.Model.Responses.Streaming.ResponseCreated
        {
            SequenceNumber = seq++,
            Response = baseResponse
        };

        Common.Model.Responses.Streaming.ResponseStreamPart? errorPart = null;
        TranslationResult? result = null;
        try
        {
            result = await TranslateAsync(texts, options.Model?.Split("/").Last()!, cancellationToken);
        }
        catch (Exception ex)
        {
            errorPart = new Common.Model.Responses.Streaming.ResponseError
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

            yield return new Common.Model.Responses.Streaming.ResponseOutputTextDelta
            {
                SequenceNumber = seq++,
                ItemId = itemId,
                ContentIndex = 0,
                Outputindex = 0,
                Delta = piece
            };
        }

        var joined = string.Join("\n", full);

        yield return new Common.Model.Responses.Streaming.ResponseOutputTextDone
        {
            SequenceNumber = seq++,
            ItemId = itemId,
            ContentIndex = 0,
            Outputindex = 0,
            Text = joined
        };

        yield return new Common.Model.Responses.Streaming.ResponseCompleted
        {
            SequenceNumber = seq++,
            Response = new Common.Model.Responses.ResponseResult
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

