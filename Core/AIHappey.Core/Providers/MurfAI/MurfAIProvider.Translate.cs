using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Responses;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MurfAI;

public sealed partial class MurfAIProvider
{
    private sealed class TranslationResult
    {
        public required IReadOnlyList<TranslationItem> Translations { get; init; }
        public JsonElement? Raw { get; init; }
    }

    private sealed class TranslationItem
    {
        public required string SourceText { get; init; }
        public required string TranslatedText { get; init; }
    }

    private static bool TryGetTranslateLanguage(string? model, string? providerLanguage, out string? language)
    {
        language = null;

        if (string.IsNullOrWhiteSpace(model))
            return false;

        var m = model.Trim();
        if (!m.Equals("translate", StringComparison.OrdinalIgnoreCase) &&
            !m.StartsWith("translate/", StringComparison.OrdinalIgnoreCase))
            return false;

        // allow model suffix: translate/<lang>
        if (m.StartsWith("translate/", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = m["translate/".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(suffix))
            {
                language = suffix;
                return true;
            }
        }

        // fallback: metadata-provided
        if (!string.IsNullOrWhiteSpace(providerLanguage))
        {
            language = providerLanguage!.Trim();
            return true;
        }

        throw new ArgumentException(
            "MurfAI translate requires a target language. Provide provider metadata { murfai: { language: 'es-ES' } } or use model 'translate/<lang>'.",
            nameof(model));
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

    private async Task<TranslationResult> TranslateAsync(IReadOnlyList<string> texts,
        string language, CancellationToken cancellationToken)
    {
        if (texts is null) throw new ArgumentNullException(nameof(texts));
        if (texts.Count == 0) throw new ArgumentException("At least one text is required.", nameof(texts));
        if (string.IsNullOrWhiteSpace(language)) throw new ArgumentException("Language is required.", nameof(language));

        var payload = new Dictionary<string, object?>
        {
            ["targetLanguage"] = language,
            ["texts"] = texts
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/text/translate")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MurfAI translate failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var translations = new List<TranslationItem>();
        if (root.TryGetProperty("translations", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                var src = el.TryGetProperty("source_text", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                var tr = el.TryGetProperty("translated_text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

                translations.Add(new TranslationItem
                {
                    SourceText = src ?? string.Empty,
                    TranslatedText = tr ?? string.Empty
                });
            }
        }

        return new TranslationResult
        {
            Translations = translations,
            Raw = root.Clone()
        };
    }

    private async IAsyncEnumerable<UIMessagePart> StreamTranslateAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Translate each incoming text part from the last user message.
        var lastUser = chatRequest.Messages?.LastOrDefault(m => m.Role == Role.user);
        var texts = lastUser?.Parts?.OfType<TextUIPart>()
            .Select(p => p.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList() ?? [];

        if (texts.Count == 0)
        {
            yield return "No prompt provided.".ToErrorUIPart();
            yield break;
        }

        TranslationResult? result = null;
        string? error = null;
        try
        {
            result = await TranslateAsync(texts, chatRequest.Model.Split("/").Last(), cancellationToken);
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

        for (var i = 0; i < result?.Translations.Count; i++)
        {
            var text = result.Translations[i].TranslatedText;
            var delta = (i == result.Translations.Count - 1) ? text : (text + "\n");
            yield return new TextDeltaUIMessageStreamPart { Id = id, Delta = delta };
        }

        yield return id.ToTextEndUIMessageStreamPart();
        yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
    }
}

