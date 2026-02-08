using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using GTranslate.Translators;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.GTranslate;

public partial class GTranslateProvider
{
    private enum GTranslateBackend
    {
        Yandex,
        GoogleV1,
        GoogleV2,
        Bing,
        Microsoft
    }

    private static (GTranslateBackend Backend, string TargetLanguage) ParseModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model is required.", nameof(modelId));

        var m = modelId.Trim();

        var patterns = new (string Prefix, GTranslateBackend Backend)[]
        {
            ("yandex/translate-to/", GTranslateBackend.Yandex),
            ("google/v1/translate-to/", GTranslateBackend.GoogleV1),
            ("google/v2/translate-to/", GTranslateBackend.GoogleV2),
            ("bing/translate-to/", GTranslateBackend.Bing),
            ("microsoft/translate-to/", GTranslateBackend.Microsoft)
        };

        foreach (var (prefix, backend) in patterns)
        {
            if (!m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var lang = m[prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(lang))
                throw new ArgumentException("GTranslate target language is missing from model id.", nameof(modelId));

            return (backend, lang);
        }

        throw new ArgumentException($"GTranslate model id not recognized: '{modelId}'.", nameof(modelId));
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

    private static ITranslator CreateTranslator(GTranslateBackend backend)
        => backend switch
        {
            GTranslateBackend.Yandex => new YandexTranslator(),
            GTranslateBackend.GoogleV1 => new GoogleTranslator(),
            GTranslateBackend.GoogleV2 => new GoogleTranslator2(),
            GTranslateBackend.Bing => new BingTranslator(),
            GTranslateBackend.Microsoft => new MicrosoftTranslator(),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null)
        };

    private async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts,
        string modelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) throw new ArgumentException("At least one text is required.", nameof(texts));

        var (backend, targetLanguage) = ParseModel(modelId);
        var translator = CreateTranslator(backend);

        var translated = new List<string>(texts.Count);
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await translator.TranslateAsync(text, targetLanguage);
            translated.Add(result?.Translation ?? string.Empty);
        }

        return translated;
    }

    internal async Task<CreateMessageResult> TranslateSamplingAsync(
        CreateMessageRequestParams chatRequest,
        string modelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var texts = chatRequest.Messages
            .Where(m => m.Role == ModelContextProtocol.Protocol.Role.User)
            .SelectMany(m => m.Content.OfType<TextContentBlock>())
            .Select(b => b.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = await TranslateAsync(texts, modelId, cancellationToken);
        var joined = string.Join("\n", translated);

        return new CreateMessageResult
        {
            Role = ModelContextProtocol.Protocol.Role.Assistant,
            Model = modelId,
            StopReason = "stop",
            Content = [joined.ToTextContentBlock()]
        };
    }

    internal async Task<ResponseResult> TranslateResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model ?? throw new ArgumentException(nameof(options.Model));
        var texts = ExtractResponseRequestTexts(options);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = await TranslateAsync(texts, modelId, cancellationToken);
        var joined = string.Join("\n", translated);

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

    internal async IAsyncEnumerable<UIMessagePart> StreamTranslateAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        // Translate each incoming text part from the last user message.
        var lastUser = chatRequest.Messages?.LastOrDefault(m => m.Role == Vercel.Models.Role.user);
        var texts = lastUser?.Parts?.OfType<TextUIPart>()
            .Select(p => p.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList() ?? [];

        if (texts.Count == 0)
        {
            yield return "No prompt provided.".ToErrorUIPart();
            yield break;
        }

        IReadOnlyList<string>? translated = null;
        string? error = null;

        try
        {
            translated = await TranslateAsync(texts, chatRequest.Model, cancellationToken);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            yield return error!.ToErrorUIPart();
            yield break;
        }

        var id = Guid.NewGuid().ToString("n");
        yield return id.ToTextStartUIMessageStreamPart();

        for (var i = 0; i < translated!.Count; i++)
        {
            var text = translated[i];
            var delta = (i == translated.Count - 1) ? text : (text + "\n");
            yield return new TextDeltaUIMessageStreamPart { Id = id, Delta = delta };
        }

        yield return id.ToTextEndUIMessageStreamPart();
        yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
    }
}
