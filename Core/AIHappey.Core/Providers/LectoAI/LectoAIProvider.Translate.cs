using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.LectoAI;

public sealed partial class LectoAIProvider
{
    private static string GetTranslateTargetLanguageFromModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var m = model.Trim();

        const string prefix = "translate/";
        if (!m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"LectoAI translation model must start with '{prefix}'. Got '{model}'.", nameof(model));

        var lang = m[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(lang))
            throw new ArgumentException("LectoAI translation target language is missing from model id.", nameof(model));

        return lang;
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

    private static List<string> NormalizeStableList(IReadOnlyList<string> translated, int expectedCount)
    {
        var result = new List<string>(expectedCount);
        for (var i = 0; i < expectedCount; i++)
        {
            var t = i < translated.Count ? translated[i] : null;
            result.Add(t ?? string.Empty);
        }
        return result;
    }

    private static bool TryGetStringArray(JsonElement el, out List<string> values)
    {
        values = [];

        if (el.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                values.Add(item.GetString() ?? string.Empty);
            else
                values.Add(item.ToString());
        }

        return values.Count > 0;
    }

    private static List<string> ExtractTranslations(JsonElement root, int expectedCount)
    {
        // Try a few common shapes, keeping output order stable.
        // 1) { "translations": ["...", "..."] }
        if (root.TryGetProperty("translations", out var tEl))
        {
            if (TryGetStringArray(tEl, out var direct))
                return NormalizeStableList(direct, expectedCount);

            // 2) { "translations": { "de": ["...", "..."] } }
            if (tEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in tEl.EnumerateObject())
                {
                    if (TryGetStringArray(prop.Value, out var arr))
                        return NormalizeStableList(arr, expectedCount);
                }
            }

            // 3) { "translations": [ { "translated_texts": [..] } ] }
            if (tEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    if (item.TryGetProperty("translated_texts", out var tt) && TryGetStringArray(tt, out var arr))
                        return NormalizeStableList(arr, expectedCount);

                    if (item.TryGetProperty("texts", out var texts) && TryGetStringArray(texts, out var arr2))
                        return NormalizeStableList(arr2, expectedCount);
                }

                // 4) { "translations": [ { "translated_text": "..." }, ... ] }
                var flattened = new List<string>(expectedCount);
                foreach (var item in tEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;

                    var str =
                        (item.TryGetProperty("translated_text", out var a) && a.ValueKind == JsonValueKind.String) ? a.GetString() :
                        (item.TryGetProperty("translatedText", out var b) && b.ValueKind == JsonValueKind.String) ? b.GetString() :
                        (item.TryGetProperty("translation", out var c) && c.ValueKind == JsonValueKind.String) ? c.GetString() :
                        (item.TryGetProperty("text", out var d) && d.ValueKind == JsonValueKind.String) ? d.GetString() :
                        null;

                    if (str is not null)
                        flattened.Add(str);
                }

                if (flattened.Count > 0)
                    return NormalizeStableList(flattened, expectedCount);
            }
        }

        // 5) { "translated_texts": ["...", "..."] }
        if (root.TryGetProperty("translated_texts", out var ttEl) && TryGetStringArray(ttEl, out var direct2))
            return NormalizeStableList(direct2, expectedCount);

        // Fallback: no recognizable payload.
        return [.. Enumerable.Range(0, expectedCount).Select(_ => string.Empty)];
    }

    private async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) throw new ArgumentException("At least one text is required.", nameof(texts));
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new ArgumentException("Target language is required.", nameof(targetLanguage));

        // LectoAI docs: POST /v1/translate/text
        // We translate to a single target language derived from the model id.
        var payload = new Dictionary<string, object?>
        {
            // "from" omitted: server auto-detects.
            ["texts"] = texts,
            ["to"] = new[] { targetLanguage.Trim() },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/translate/text")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"LectoAI translate failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var translated = ExtractTranslations(doc.RootElement, texts.Count);
        return translated;
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

