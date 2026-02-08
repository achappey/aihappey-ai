using AIHappey.Core.AI;
using AIHappey.Responses;
using Azure;
using Azure.AI.Translation.Text;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{
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

