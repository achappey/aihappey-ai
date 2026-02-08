using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.GoogleTranslate;

public sealed partial class GoogleTranslateProvider
{
    private static string GetTranslateTargetLanguageFromModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var m = model.Trim();

        const string prefix = "translate/";
        if (!m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"GoogleTranslate translation model must start with '{prefix}'. Got '{model}'.", nameof(model));

        var lang = m[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(lang))
            throw new ArgumentException("GoogleTranslate translation target language is missing from model id.", nameof(model));

        return lang;
    }

    private sealed class TranslateResponse
    {
        public TranslateData? Data { get; set; }
    }

    private sealed class TranslateData
    {
        public List<TranslateItem>? Translations { get; set; }
    }

    private sealed class TranslateItem
    {
        public string? TranslatedText { get; set; }
        public string? DetectedSourceLanguage { get; set; }
        public string? Model { get; set; }
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

    private async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) throw new ArgumentException("At least one text is required.", nameof(texts));
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new ArgumentException("Target language is required.", nameof(targetLanguage));

        var key = GetKeyOrThrow();
        var url = $"{TranslationV2BaseUrl}?key={Uri.EscapeDataString(key)}";

        var payload = new Dictionary<string, object?>
        {
            // Google accepts string or array; use array always for simplicity.
            ["q"] = texts,
            ["target"] = targetLanguage.Trim(),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GoogleTranslate translate failed ({(int)resp.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<TranslateResponse>(body, JsonSerializerOptions.Web);
        var translations = parsed?.Data?.Translations ?? [];

        // Expected one output per input.
        if (translations.Count == 0)
            return [.. texts.Select(_ => string.Empty)];

        // If API returns fewer items than input, keep stable output shape.
        var result = new List<string>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var t = (i < translations.Count)
                ? translations[i].TranslatedText
                : null;
            result.Add(t ?? string.Empty);
        }

        return result;
    }

    internal async Task<CreateMessageResult> TranslateSamplingAsync(
        CreateMessageRequestParams chatRequest,
        string modelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var targetLanguage = GetTranslateTargetLanguageFromModel(modelId);

        var texts = chatRequest.Messages
            .Where(m => m.Role == ModelContextProtocol.Protocol.Role.User)
            .SelectMany(m => m.Content.OfType<TextContentBlock>())
            .Select(b => b.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = await TranslateAsync(texts, targetLanguage, cancellationToken);
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
        var targetLanguage = GetTranslateTargetLanguageFromModel(modelId);

        var texts = ExtractResponseRequestTexts(options);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = await TranslateAsync(texts, targetLanguage, cancellationToken);
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

        var targetLanguage = GetTranslateTargetLanguageFromModel(chatRequest.Model);

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
            translated = await TranslateAsync(texts, targetLanguage, cancellationToken);
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

