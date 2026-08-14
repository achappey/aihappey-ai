using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using Azure;
using Azure.AI.Translation.Text;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{
    private async Task<AIResponse> ExecuteTranslationUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        var texts = ExtractUnifiedRequestTexts(request);
        if (texts.Count == 0)
            throw new ArgumentException("Azure translation requires text in the latest user message.", nameof(request));

        var translated = await TranslateAsync(
            texts,
            GetTranslateTargetLanguageFromModel(request.Model!),
            cancellationToken);

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model,
            Status = "completed",
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = [new AITextContentPart { 
                            Type = "text",
                            Text = string.Join("\n", translated) }]
                    }
                ]
            }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamTranslationUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteTranslationUnifiedAsync(request, cancellationToken);
        var id = request.Id ?? Guid.NewGuid().ToString("N");
        var text = response.Output?.Items?
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .FirstOrDefault()?.Text ?? string.Empty;

        yield return CreateTranslationStreamEvent(id, "text-start", new AITextStartEventData());
        if (!string.IsNullOrEmpty(text))
            yield return CreateTranslationStreamEvent(id, "text-delta", new AITextDeltaEventData { Delta = text });
        yield return CreateTranslationStreamEvent(id, "text-end", new AITextEndEventData());
        yield return CreateTranslationStreamEvent(id, "finish", new AIFinishEventData
        {
            FinishReason = "stop",
            Model = request.Model
        });
    }

    private AIStreamEvent CreateTranslationStreamEvent(string id, string type, object data)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope
            {
                Id = id,
                Type = type,
                Timestamp = DateTimeOffset.UtcNow,
                Data = data
            }
        };

    private static List<string> ExtractUnifiedRequestTexts(AIRequest request)
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

    private static string GetTranslateTargetLanguageFromModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var m = model.Contains('/')
            ? model.SplitModelId().Model
            : model;

        m = m.Trim();

        const string prefix = "translate-to-";
        if (!m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Azure translation model must start with '{prefix}'. Got '{model}'.", nameof(model));

        var lang = m[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(lang))
            throw new ArgumentException("Azure translation target language is missing from model id.", nameof(model));

        return lang;
    }

    private TextTranslationClient CreateTextTranslationClient()
    {
        var credential = new AzureKeyCredential(GetKey());
        return new TextTranslationClient(
            credential,
            region: GetEndpointRegion(),
            options: new TextTranslationClientOptions());
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
        List<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) throw new ArgumentException("At least one text is required.", nameof(texts));
        if (string.IsNullOrWhiteSpace(targetLanguage)) throw new ArgumentException("Target language is required.", nameof(targetLanguage));

        var client = CreateTextTranslationClient();
        var resp = await client.TranslateAsync(targetLanguage.Trim(), texts, cancellationToken: cancellationToken);

        // The SDK returns one item per input string.
        var translated = new List<string>(texts.Count);
        foreach (var item in resp.Value)
        {
            // Translations are returned as a list (usually 1 per requested target language).
            var t = item.Translations.FirstOrDefault()?.Text;
            translated.Add(t ?? string.Empty);
        }

        return translated;
    }
}

