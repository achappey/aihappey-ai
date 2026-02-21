using AIHappey.Core.AI;
using AIHappey.Core.Models;
using GTranslate;

namespace AIHappey.Core.Providers.GTranslate;

public partial class GTranslateProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {

        return await ListTranslationModelsAsync(cancellationToken);
    }

    private async Task<IEnumerable<Model>> ListTranslationModelsAsync(CancellationToken cancellationToken)
    {
        var yandexModels = Language.LanguageDictionary.Select(a => new Model()
        {
            OwnedBy = nameof(GTranslate),
            Type = "language",
            Id = $"yandex/translate-to/{a.Key}".ToModelId(GetIdentifier()),
            Name = $"Yandex Translate to {a.Value.Name}",
            Description = a.Value.NativeName,
        }).ToList();

        var bingModels = Language.LanguageDictionary.Select(a => new Model()
        {
            OwnedBy = nameof(GTranslate),
            Type = "language",
            Id = $"bing/translate-to/{a.Key}".ToModelId(GetIdentifier()),
            Name = $"Bing Translate to {a.Value.Name}",
            Description = a.Value.NativeName,
        }).ToList();

        var microsoftModels = Language.LanguageDictionary.Select(a => new Model()
        {
            OwnedBy = nameof(GTranslate),
            Type = "language",
            Id = $"microsoft/translate-to/{a.Key}".ToModelId(GetIdentifier()),
            Name = $"Microsoft Translate to {a.Value.Name}",
            Description = a.Value.NativeName,
        }).ToList();

        var googleModels = Language.LanguageDictionary.Select(a => new Model()
        {
            OwnedBy = nameof(GTranslate),
            Type = "language",
            Id = $"google/v1/translate-to/{a.Key}".ToModelId(GetIdentifier()),
            Name = $"Google Translate to {a.Value.Name}",
            Description = a.Value.NativeName,
        }).ToList();

        var google2Models = Language.LanguageDictionary.Select(a => new Model()
        {
            OwnedBy = nameof(GTranslate),
            Type = "language",
            Id = $"google/v2/translate-to/{a.Key}".ToModelId(GetIdentifier()),
            Name = $"Google Translate to {a.Value.Name}",
            Description = a.Value.NativeName,
        }).ToList();

        return await Task.FromResult<IEnumerable<Model>>([.. yandexModels, .. googleModels, .. google2Models, .. bingModels, .. microsoftModels]);
    }
}

