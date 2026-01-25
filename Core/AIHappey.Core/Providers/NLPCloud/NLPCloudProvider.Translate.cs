using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    private const string NllbTranslationModelPrefix = "nllb-200-3-3b/translate-to/";

    private sealed record NLPCloudTranslationRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("target")] string Target,
        [property: JsonPropertyName("source")] string Source);

    private sealed record NLPCloudTranslationResponse(
        [property: JsonPropertyName("translation_text")] string TranslationText);

    private static string GetTranslationTargetLanguageFromModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var m = model.Trim();
        if (!m.StartsWith(NllbTranslationModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"NLPCloud translation model must start with '{NllbTranslationModelPrefix}'. Got '{model}'.", nameof(model));

        var lang = m[NllbTranslationModelPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(lang))
            throw new ArgumentException("NLPCloud translation target language is missing from model id.", nameof(model));

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

    private static string BuildTranslationInput(IReadOnlyList<(string Role, string? Content)> messages)
    {
        if (messages.Count == 0)
            throw new ArgumentException("No messages provided.", nameof(messages));

        var lastUser = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(lastUser.Content))
            return lastUser.Content!;

        var lastContent = messages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Content));
        if (!string.IsNullOrWhiteSpace(lastContent.Content))
            return lastContent.Content!;

        throw new ArgumentException("No input text provided for translation.", nameof(messages));
    }

    private async Task<string> SendTranslationAsync(
        string model,
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var baseModel = model.Trim();
        if (baseModel.StartsWith(NllbTranslationModelPrefix, StringComparison.OrdinalIgnoreCase))
            baseModel = "nllb-200-3-3b";

        var relativeUrl = $"{baseModel}/translation";
        var payload = new NLPCloudTranslationRequest(text, targetLanguage, string.Empty);
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"NLPCloud API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<NLPCloudTranslationResponse>(
            stream,
            JsonSerializerOptions.Web,
            cancellationToken).ConfigureAwait(false);

        if (result is null || string.IsNullOrWhiteSpace(result.TranslationText))
            throw new InvalidOperationException("Empty NLPCloud translation response.");

        return result.TranslationText;
    }

    private async IAsyncEnumerable<string> StreamTranslationAsync(
        string model,
        string text,
        string targetLanguage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await SendTranslationAsync(model, text, targetLanguage, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result))
            yield return result;
    }

    internal async Task<CreateMessageResult> TranslateSamplingAsync(
        CreateMessageRequestParams chatRequest,
        string modelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var targetLanguage = GetTranslationTargetLanguageFromModel(modelId);

        var texts = chatRequest.Messages
            .Where(m => m.Role == ModelContextProtocol.Protocol.Role.User)
            .SelectMany(m => m.Content.OfType<TextContentBlock>())
            .Select(b => b.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translations = new List<string>(texts.Count);
        foreach (var text in texts)
        {
            translations.Add(await SendTranslationAsync(modelId, text, targetLanguage, cancellationToken).ConfigureAwait(false));
        }

        var joined = string.Join("\n", translations);
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
        var targetLanguage = GetTranslationTargetLanguageFromModel(modelId);

        var texts = ExtractResponseRequestTexts(options);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translations = new List<string>(texts.Count);
        foreach (var text in texts)
        {
            translations.Add(await SendTranslationAsync(modelId, text, targetLanguage, cancellationToken).ConfigureAwait(false));
        }

        var joined = string.Join("\n", translations);
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

        var targetLanguage = GetTranslationTargetLanguageFromModel(chatRequest.Model);

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
            var translations = new List<string>(texts.Count);
            foreach (var text in texts)
            {
                translations.Add(await SendTranslationAsync(chatRequest.Model, text, targetLanguage, cancellationToken).ConfigureAwait(false));
            }
            translated = translations;
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
