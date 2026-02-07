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

namespace AIHappey.Core.Providers.Sarvam;

public partial class SarvamProvider
{
    private const string MayuraTranslatePrefix = "mayura:v1/translate-to/";
    private const string SarvamTranslatePrefix = "sarvam-translate:v1/translate/";

    private sealed class SarvamTranslateResponse
    {
        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }

        [JsonPropertyName("translated_text")]
        public string? TranslatedText { get; set; }

        [JsonPropertyName("source_language_code")]
        public string? SourceLanguageCode { get; set; }
    }

    private static (string Model, string SourceLanguage, string TargetLanguage) ParseTranslationModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model is required.", nameof(modelId));

        var normalized = modelId.Trim();

        if (normalized.StartsWith(MayuraTranslatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var target = normalized[MayuraTranslatePrefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("Mayura translation target language is missing from model id.", nameof(modelId));

            return ("mayura:v1", "auto", target);
        }

        if (normalized.StartsWith(SarvamTranslatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var remainder = normalized[SarvamTranslatePrefix.Length..].Trim();
            var parts = remainder.Split("/to/", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                throw new ArgumentException("Sarvam translation model id must be 'sarvam-translate:v1/translate/{source}/to/{target}'.", nameof(modelId));

            return ("sarvam-translate:v1", parts[0], parts[1]);
        }

        throw new ArgumentException($"Sarvam translation model is invalid: '{modelId}'.", nameof(modelId));
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

    private async Task<string> TranslateAsync(
        string text,
        string modelId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Input text is required.", nameof(text));

        var (model, sourceLanguage, targetLanguage) = ParseTranslationModel(modelId);

        var payload = new Dictionary<string, object?>
        {
            ["input"] = text,
            ["source_language_code"] = sourceLanguage,
            ["target_language_code"] = targetLanguage,
            ["model"] = model
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "translate")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sarvam translate failed ({(int)resp.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<SarvamTranslateResponse>(body, JsonSerializerOptions.Web);
        return parsed?.TranslatedText ?? string.Empty;
    }

    internal async Task<CreateMessageResult> TranslateSamplingAsync(
        CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var modelId = chatRequest.GetModel();
        ArgumentNullException.ThrowIfNull(modelId);

        var texts = chatRequest.Messages
            .Where(m => m.Role == ModelContextProtocol.Protocol.Role.User)
            .SelectMany(m => m.Content.OfType<TextContentBlock>())
            .Select(b => b.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = new List<string>(texts.Count);

        foreach (var text in texts)
            translated.Add(await TranslateAsync(text, modelId, cancellationToken));

        return new CreateMessageResult
        {
            Role = ModelContextProtocol.Protocol.Role.Assistant,
            Model = modelId,
            StopReason = "stop",
            Content = [.. translated.Select(a => a.ToTextContentBlock())]
        };
    }

    internal async Task<ResponseResult> TranslateResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model ?? throw new ArgumentException(nameof(options.Model));
        var now = DateTimeOffset.UtcNow;

        var texts = ExtractResponseRequestTexts(options);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = new List<string>(texts.Count);
        foreach (var text in texts)
            translated.Add(await TranslateAsync(text, modelId, cancellationToken));

        var joined = string.Join("\n", translated);

        return new ResponseResult
        {
            Id = Guid.NewGuid().ToString("n"),
            Model = modelId,
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

    internal async IAsyncEnumerable<UIMessagePart> StreamTranslateAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

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
            var list = new List<string>(texts.Count);
            foreach (var text in texts)
                list.Add(await TranslateAsync(text, chatRequest.Model, cancellationToken));

            translated = list;
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
