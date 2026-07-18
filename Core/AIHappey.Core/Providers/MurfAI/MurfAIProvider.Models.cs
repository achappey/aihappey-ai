
using AIHappey.Core.Models;
using AIHappey.Core.AI;


namespace AIHappey.Core.Providers.MurfAI;

public sealed partial class MurfAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(
        CancellationToken cancellationToken = default)
    {
        var models = await this.ListModels(
            _keyResolver.Resolve(GetIdentifier()));

        return models.Concat(CreateMurfTranslationModels());
    }

    private static IEnumerable<Model> CreateMurfTranslationModels()
    {
        foreach (var (locale, name) in MurfTranslationModels)
        {
            yield return new Model
            {
                Id = $"murfai/translate/{locale}",
                Name = name,
                Type = "language",
                OwnedBy = "MurfAI",
                Tags = ["translate", locale.NormalizeLanguageCode()],
                Description = $"Translate to {name}."
            };
        }
    }

    private static readonly IReadOnlyDictionary<string, string> MurfTranslationModels =
new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["en-US"] = "English (US & Canada)",
    ["en-UK"] = "English (UK)",
    ["en-IN"] = "English (India)",
    ["en-AU"] = "English (Australia)",
    ["en-SCOTT"] = "English (Scotland)",
    ["es-MX"] = "Spanish (Mexico)",
    ["es-ES"] = "Spanish (Spain)",
    ["fr-FR"] = "French (France)",
    ["de-DE"] = "German (Germany)",
    ["it-IT"] = "Italian (Italy)",
    ["nl-NL"] = "Dutch (Netherlands)",
    ["pt-BR"] = "Portuguese (Brazil)",
    ["zh-CN"] = "Chinese (Mandarin, China)",
    ["ja-JP"] = "Japanese (Japan)",
    ["ko-KR"] = "Korean (Korea)",
    ["hi-IN"] = "Hindi (India)",
    ["ta-IN"] = "Tamil (India)",
    ["bn-IN"] = "Bangla (India)",
    ["hr-HR"] = "Croatian (Croatia)",
    ["sk-SK"] = "Slovak (Slovakia)",
    ["pl-PL"] = "Polish (Poland)",
    ["el-GR"] = "Greek (Greece)"
};
}

