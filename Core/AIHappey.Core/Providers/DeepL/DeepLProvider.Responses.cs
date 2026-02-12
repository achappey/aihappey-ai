using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.DeepL;

public partial class DeepLProvider
{
    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(options);

        return await TranslateResponsesAsync(options, cancellationToken);
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

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
            Model = modelId,
            CreatedAt = createdAt,
            Output = []
        };

        yield return new ResponseCreated
        {
            SequenceNumber = seq++,
            Response = baseResponse
        };

        ResponseStreamPart? errorPart = null;
        IReadOnlyList<string>? translated = null;
        try
        {
            translated = await ProcessTextsAsync(texts, modelId, cancellationToken);
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

        var responseItemId = Guid.NewGuid().ToString("n");

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
                        id = responseItemId,
                        status = "completed",
                        role = "assistant",
                        content = parts
                    }
                ]
            }
        };
    }


    private async Task<ResponseResult> TranslateResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model ?? throw new ArgumentException(nameof(options.Model));
        var texts = ExtractResponseRequestTexts(options);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = await ProcessTextsAsync(texts, modelId, cancellationToken);

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


    private static List<string> ExtractResponseRequestTexts(ResponseRequest options)
    {
        var texts = new List<string>();

        if (options.Input?.IsText == true)
        {
            if (!string.IsNullOrWhiteSpace(options.Input.Text))
                texts.Add(options.Input.Text!);
            return texts;
        }

        var items = options.Input?.Items;
        if (items is null) return texts;

        foreach (var msg in items.OfType<ResponseInputMessage>().Where(m => m.Role == ResponseRole.User))
        {
            if (msg.Content.IsText)
            {
                if (!string.IsNullOrWhiteSpace(msg.Content.Text))
                    texts.Add(msg.Content.Text!);
            }
            else if (msg.Content.IsParts)
            {
                foreach (var p in msg.Content.Parts!.OfType<InputTextPart>())
                {
                    if (!string.IsNullOrWhiteSpace(p.Text))
                        texts.Add(p.Text);
                }
            }
        }

        return texts;
    }

}
