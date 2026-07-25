using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Lara;

public partial class LaraProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                var languages = await CreateTranslator().Languages();

                return languages
                    .Where(language => !string.IsNullOrWhiteSpace(language))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
                    .Select(language =>
                    {
                        var locale = language.Trim();
                        return new Model
                        {
                            OwnedBy = "Lara",
                            Type = "language",
                            Id = $"translate/{locale}".ToModelId(GetIdentifier()),
                            Name = $"Translate to {GetDisplayLanguageName(locale)}",
                            Description = locale,
                            Tags = [locale.NormalizeLanguageCode(), "translate"]
                        };
                    })
                    .ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static string GetDisplayLanguageName(string locale)
    {
        try
        {
            return CultureInfo.GetCultureInfo(locale.Replace('-', '_')).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return locale;
        }
    }
}
