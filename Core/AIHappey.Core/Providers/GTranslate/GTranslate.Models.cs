using AIHappey.Core.AI;
using AIHappey.Core.Models;
using GTranslate;

namespace AIHappey.Core.Providers.GTranslate;

public partial class GTranslateProvider
{
    public Task<IEnumerable<Model>> ListModels(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var models = new List<Model>();

        models.AddRange(CreateTranslationModels(
            service: TranslationServices.Yandex,
            modelPrefix: "yandex/translate-to/",
            displayName: "Yandex"));

        models.AddRange(CreateTranslationModels(
            service: TranslationServices.Google,
            modelPrefix: "google/v1/translate-to/",
            displayName: "Google"));

        models.AddRange(CreateTranslationModels(
            service: TranslationServices.Google,
            modelPrefix: "google/v2/translate-to/",
            displayName: "Google V2"));

        models.AddRange(CreateTranslationModels(
            service: TranslationServices.Bing,
            modelPrefix: "bing/translate-to/",
            displayName: "Bing"));

        models.AddRange(CreateTranslationModels(
            service: TranslationServices.Microsoft,
            modelPrefix: "microsoft/translate-to/",
            displayName: "Microsoft"));

        return Task.FromResult<IEnumerable<Model>>(models);
    }

    private IEnumerable<Model> CreateTranslationModels(
      TranslationServices service,
      string modelPrefix,
      string displayName)
    {
        return Language.LanguageDictionary
            .Where(entry => entry.Value.IsServiceSupported(service))
            .Select(entry =>
            {
                var languageCode = entry.Key;
                var language = entry.Value;

                return new Model
                {
                    OwnedBy = nameof(GTranslate),
                    Type = "language",
                    Tags =
                    [
                        languageCode.NormalizeLanguageCode(),
                        "translate"
                    ],
                    Id = $"{modelPrefix}{languageCode}"
                        .ToModelId(GetIdentifier()),
                    Name = $"{displayName} Translate to {language.Name}",
                    Description = language.NativeName
                };
            });
    }
}

