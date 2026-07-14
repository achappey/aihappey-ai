using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.LelapaAI;

public partial class LelapaAIProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);

        var modelId = request.Model ?? $"{GetIdentifier()}/{VulavulaModel}";
        var texts = ExtractUnifiedRequestTexts(request);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = await TranslateAsync(texts, modelId, request.Metadata, cancellationToken);
        var joined = string.Join("\n", translated.Texts);

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = modelId,
            Status = "completed",
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Text = joined,
                                Type = "text",
                                Metadata = CreateTranslationMetadata(translated.SourceLanguage, translated.TargetLanguage, translated.RawRoot)
                            }
                        ],
                        Metadata = CreateTranslationMetadata(translated.SourceLanguage, translated.TargetLanguage, translated.RawRoot)
                    }
                ]
            },
            Metadata = CreateTranslationMetadata(translated.SourceLanguage, translated.TargetLanguage, translated.RawRoot)
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var text = response.Output?.Items?
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .FirstOrDefault() ?? string.Empty;

        var providerId = GetIdentifier();
        var eventId = Guid.NewGuid().ToString("n");
        var timestamp = DateTimeOffset.UtcNow;

        yield return CreateTextStreamEvent(providerId, eventId, "text-start", new AITextStartEventData(), timestamp, response.Metadata);
        yield return CreateTextStreamEvent(providerId, eventId, "text-delta", new AITextDeltaEventData { Delta = text }, timestamp, response.Metadata);
        yield return CreateTextStreamEvent(providerId, eventId, "text-end", new AITextEndEventData(), timestamp, response.Metadata);
        yield return CreateTextStreamEvent(
            providerId,
            eventId,
            "finish",
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = response.Model,
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(response.Model ?? $"{providerId}/{VulavulaModel}", DateTimeOffset.UtcNow)
            },
            DateTimeOffset.UtcNow,
            response.Metadata);
    }

    private static AIStreamEvent CreateTextStreamEvent(
        string providerId,
        string eventId,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = providerId,
            Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = eventId,
                Timestamp = timestamp,
                Data = data,
                Metadata = metadata
            }
        };

    private async Task<LelapaAITranslateResult> TranslateAsync(
        IReadOnlyList<string> texts,
        string modelId,
        Dictionary<string, object?>? metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
            throw new ArgumentException("At least one text is required.", nameof(texts));

        var (sourceLanguage, targetLanguage) = ResolveTranslationLanguages(modelId, GetIdentifier(), metadata);
        var translatedTexts = new List<string>(texts.Count);
        JsonElement lastRoot = default;

        foreach (var text in texts)
        {
            var payload = new Dictionary<string, object?>
            {
                ["input_text"] = text,
                [SourceLanguageKey] = sourceLanguage,
                [TargetLanguageKey] = targetLanguage
            };

            var payloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/translate/process")
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };

            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"LelapaAI translate failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            lastRoot = doc.RootElement.Clone();
            translatedTexts.Add(ExtractTranslatedText(doc.RootElement));
        }

        return new LelapaAITranslateResult(translatedTexts, sourceLanguage, targetLanguage, lastRoot);
    }

    private static string ExtractTranslatedText(JsonElement root)
    {
        if (root.TryGetProperty("translation", out var translations)
            && translations.ValueKind == JsonValueKind.Array)
        {
            foreach (var translation in translations.EnumerateArray())
            {
                if (translation.ValueKind == JsonValueKind.Object
                    && translation.TryGetProperty("translated_text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }

    private static List<string> ExtractUnifiedRequestTexts(AIRequest request)
    {
        var texts = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            texts.Add(request.Input.Text!);

        if (request.Input?.Items is null)
            return texts;

        foreach (var item in request.Input.Items)
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var textPart in item.Content?.OfType<AITextContentPart>() ?? [])
            {
                if (!string.IsNullOrWhiteSpace(textPart.Text))
                    texts.Add(textPart.Text);
            }
        }

        return texts;
    }

    private static Dictionary<string, object?> CreateTranslationMetadata(
        string sourceLanguage,
        string targetLanguage,
        JsonElement rawRoot)
        => new()
        {
            [SourceLanguageKey] = sourceLanguage,
            [TargetLanguageKey] = targetLanguage,
            ["lelapaai.response.raw"] = rawRoot.ValueKind == JsonValueKind.Undefined ? null : rawRoot.Clone()
        };

    private sealed record LelapaAITranslateResult(
        IReadOnlyList<string> Texts,
        string SourceLanguage,
        string TargetLanguage,
        JsonElement RawRoot);

}
