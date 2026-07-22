using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
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

    private static List<string> ExtractLatestUserTextParts(AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return [request.Input.Text];

        return request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))?
            .Content?
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList()
            ?? [];
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

    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        var (modelId, backend, targetLanguage, translated) = await TranslateUnifiedAsync(request, cancellationToken);
        return CreateUnifiedResponse(modelId, backend, targetLanguage, translated);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (modelId, backend, targetLanguage, translated) = await TranslateUnifiedAsync(request, cancellationToken);
        var response = CreateUnifiedResponse(modelId, backend, targetLanguage, translated);
        var eventId = Guid.NewGuid().ToString("n");
        var timestamp = DateTimeOffset.UtcNow;

        yield return CreateStreamEvent(eventId, "text-start", new AITextStartEventData(), timestamp, response.Metadata);

        for (var index = 0; index < translated.Count; index++)
        {
            var delta = index == translated.Count - 1 ? translated[index] : translated[index] + "\n";
            if (!string.IsNullOrEmpty(delta))
                yield return CreateStreamEvent(eventId, "text-delta", new AITextDeltaEventData { Delta = delta }, timestamp, response.Metadata);
        }

        yield return CreateStreamEvent(eventId, "text-end", new AITextEndEventData(), timestamp, response.Metadata);
        yield return CreateStreamEvent(
            eventId,
            "finish",
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = response.Model,
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    response.Model ?? string.Empty,
                    DateTimeOffset.UtcNow,
                    response.Usage,
                    temperature: request.Temperature)
            },
            DateTimeOffset.UtcNow,
            response.Metadata);
    }

    private async Task<(string ModelId, GTranslateBackend Backend, string TargetLanguage, IReadOnlyList<string> Translated)> TranslateUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var modelId = request.Model ?? throw new ArgumentException("Model is required.", nameof(request));
        var (backend, targetLanguage) = ParseModel(modelId);
        var texts = ExtractLatestUserTextParts(request);
        if (texts.Count == 0)
            throw new ArgumentException("GTranslate requires text in the latest user message.", nameof(request));

        var translated = await TranslateAsync(texts, modelId, cancellationToken);
        return (modelId, backend, targetLanguage, translated);
    }

    private AIResponse CreateUnifiedResponse(
        string modelId,
        GTranslateBackend backend,
        string targetLanguage,
        IReadOnlyList<string> translated)
    {
        var text = string.Join("\n", translated);
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = modelId,
            Status = "completed",
            Usage = new Dictionary<string, object?>(),
            Metadata = new Dictionary<string, object?>
            {
                ["finishReason"] = "stop",
                ["backend"] = backend.ToString(),
                ["targetLanguage"] = targetLanguage
            },
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = [new AITextContentPart { Type = "text", Text = text }]
                    }
                ]
            }
        };
    }

    private AIStreamEvent CreateStreamEvent(
        string eventId,
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
                Type = type,
                Id = eventId,
                Timestamp = timestamp,
                Data = data,
                Metadata = metadata
            }
        };
}
