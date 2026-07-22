using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

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

    private static string ExtractLatestUserText(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text;

        var latestUserMessage = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var textParts = latestUserMessage?.Content?
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList() ?? [];

        if (textParts.Count == 0)
            throw new ArgumentException("Google Translate requires text in the latest user message.", nameof(request));

        return string.Join("\n", textParts);
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

    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = GetKeyOrThrow();

        var modelId = request.Model ?? throw new ArgumentException("Model is required.", nameof(request));
        var targetLanguage = GetTranslateTargetLanguageFromModel(modelId);
        var translated = await TranslateAsync([ExtractLatestUserText(request)], targetLanguage, cancellationToken);
        var text = translated.Single();

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = modelId,
            Status = "completed",
            Usage = new Dictionary<string, object?>(),
            Metadata = new Dictionary<string, object?>
            {
                ["finishReason"] = "stop",
                ["targetLanguage"] = targetLanguage
            },
            Output =
           new AIOutput
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
                            Text = text,
                            Type = "text"
                        }
                    ]
                }
            ]
           }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var text = response.Output?.Items?
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .FirstOrDefault() ?? string.Empty;
        var eventId = Guid.NewGuid().ToString("n");
        var timestamp = DateTimeOffset.UtcNow;

        yield return CreateStreamEvent(eventId, "text-start", new AITextStartEventData(), timestamp, response.Metadata);
        if (!string.IsNullOrEmpty(text))
            yield return CreateStreamEvent(eventId, "text-delta", new AITextDeltaEventData { Delta = text }, timestamp, response.Metadata);
        yield return CreateStreamEvent(eventId, "text-end", new AITextEndEventData(), timestamp, response.Metadata);
        yield return CreateStreamEvent(eventId, "finish", new AIFinishEventData
        {
            FinishReason = "stop",
            Model = response.Model,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(
                response.Model ?? string.Empty,
                DateTimeOffset.UtcNow,
                response.Usage as Dictionary<string, object?>,
                temperature: request.Temperature)
        }, DateTimeOffset.UtcNow, response.Metadata);
    }

    private AIStreamEvent CreateStreamEvent(string eventId, string type, object data, DateTimeOffset timestamp, Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
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
}

