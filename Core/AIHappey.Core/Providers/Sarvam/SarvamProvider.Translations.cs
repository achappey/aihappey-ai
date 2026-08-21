using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Unified.Models;

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
        var normalized = NormalizeModelId(modelId);

        if (normalized.StartsWith(MayuraTranslatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var target = normalized[MayuraTranslatePrefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("Mayura translation target language is missing.", nameof(modelId));

            return ("mayura:v1", "auto", target);
        }

        if (normalized.StartsWith(SarvamTranslatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var pair = normalized[SarvamTranslatePrefix.Length..]
                .Split("/to/", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
                throw new ArgumentException("Sarvam translation model must contain source and target languages.", nameof(modelId));

            return ("sarvam-translate:v1", pair[0], pair[1]);
        }

        throw new ArgumentException($"Unsupported Sarvam translation model '{modelId}'.", nameof(modelId));
    }

    private static List<string> ExtractTranslationTexts(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return [request.Input.Text];

        return request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))?
            .Content?
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList() ?? [];
    }

    private static void CopyTranslationOption(
        Dictionary<string, object?> payload,
        Dictionary<string, object?>? metadata,
        string key)
    {
        if (metadata is not null && metadata.TryGetValue(key, out var value) && value is not null)
            payload[key] = value;
    }

    private async Task<(SarvamTranslateResponse Response, string Text)> TranslateAsync(
        string text,
        string modelId,
        Dictionary<string, object?>? metadata,
        CancellationToken cancellationToken)
    {
        var (model, sourceLanguage, targetLanguage) = ParseTranslationModel(modelId);
        var payload = new Dictionary<string, object?>
        {
            ["input"] = text,
            ["source_language_code"] = sourceLanguage,
            ["target_language_code"] = targetLanguage,
            ["model"] = model
        };

        CopyTranslationOption(payload, metadata, "speaker_gender");
        CopyTranslationOption(payload, metadata, "mode");
        CopyTranslationOption(payload, metadata, "output_script");
        CopyTranslationOption(payload, metadata, "numerals_format");

        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "translate")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sarvam translate failed ({(int)response.StatusCode}): {body}");

        var result = JsonSerializer.Deserialize<SarvamTranslateResponse>(body, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Sarvam returned an invalid translation response.");
        return (result, result.TranslatedText ?? string.Empty);
    }

    internal async Task<AIResponse> ExecuteTranslationUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var modelId = request.Model ?? throw new ArgumentException("Model is required.", nameof(request));
        var texts = ExtractTranslationTexts(request);
        if (texts.Count == 0)
            throw new ArgumentException("Translation requires text in the latest user message.", nameof(request));

        var translated = new List<string>(texts.Count);
        SarvamTranslateResponse? lastResponse = null;
        foreach (var text in texts)
        {
            var result = await TranslateAsync(text, modelId, request.Metadata, cancellationToken);
            lastResponse = result.Response;
            translated.Add(result.Text);
        }

        var (_, source, target) = ParseTranslationModel(modelId);
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = modelId,
            Status = "completed",
            Usage = new Dictionary<string, object?>(),
            Metadata = new Dictionary<string, object?>
            {
                ["finishReason"] = "stop",
                ["requestId"] = lastResponse?.RequestId,
                ["sourceLanguage"] = lastResponse?.SourceLanguageCode ?? source,
                ["targetLanguage"] = target
            },
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = [new AITextContentPart { Type = "text", Text = string.Join("\n", translated) }]
                    }
                ]
            }
        };
    }

    internal async IAsyncEnumerable<AIStreamEvent> StreamTranslationUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteTranslationUnifiedAsync(request, cancellationToken);
        var text = response.Output?.Items?
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .FirstOrDefault() ?? string.Empty;
        var id = Guid.NewGuid().ToString("n");
        var timestamp = DateTimeOffset.UtcNow;

        yield return CreateTranslationStreamEvent(id, "text-start", new AITextStartEventData(), timestamp, response.Metadata);
        if (!string.IsNullOrEmpty(text))
            yield return CreateTranslationStreamEvent(id, "text-delta", new AITextDeltaEventData { Delta = text }, timestamp, response.Metadata);
        yield return CreateTranslationStreamEvent(id, "text-end", new AITextEndEventData(), timestamp, response.Metadata);
        yield return CreateTranslationStreamEvent(id, "finish", new AIFinishEventData
        {
            FinishReason = "stop",
            Model = response.Model,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model ?? string.Empty, timestamp, response.Usage, temperature: request.Temperature)
        }, DateTimeOffset.UtcNow, response.Metadata);
    }

    private AIStreamEvent CreateTranslationStreamEvent(
        string id,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Id = id,
                Type = type,
                Timestamp = timestamp,
                Data = data,
                Metadata = metadata
            }
        };
}
