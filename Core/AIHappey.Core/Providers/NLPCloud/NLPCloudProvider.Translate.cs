namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    private const string NllbTranslationModelPrefix = "nllb-200-3-3b/translate-to/";

    private static string GetTranslationTargetLanguageFromModel(string model)
    {
        var normalized = NormalizeNativeModel(model);
        if (!normalized.StartsWith(NllbTranslationModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"NLPCloud translation model must start with '{NllbTranslationModelPrefix}'.", nameof(model));

        var language = normalized[NllbTranslationModelPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("NLPCloud translation target language is missing from model id.", nameof(model));

        return language;
    }
}
