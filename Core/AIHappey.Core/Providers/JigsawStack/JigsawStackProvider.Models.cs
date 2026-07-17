using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.JigsawStack;

public partial class JigsawStackProvider
{
    public async Task<IEnumerable<Model>> ListModels2(CancellationToken cancellationToken = default)
         => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));

    public async Task<IEnumerable<Model>> ListModels(
        CancellationToken cancellationToken = default)
    {
        var models = (await this.ListModels(
            _keyResolver.Resolve(GetIdentifier()))).ToList();

        models.AddRange(
       TranslationLanguages.Select(language => new Model
       {
           Id = $"jigsawstack/translate/{language.Key}"
               .ToModelId(GetIdentifier()),
           Name = $"Translate to {language.Value}",
           Tags = ["translate", language.Key.NormalizeLanguageCode()]
       }));

        return models;
    }

    private static readonly IReadOnlyDictionary<string, string> TranslationLanguages =
new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["af"] = "Afrikaans",
    ["am"] = "Amharic",
    ["ar"] = "Arabic",
    ["as"] = "Assamese",
    ["az"] = "Azerbaijani",
    ["ba"] = "Bashkir",
    ["be"] = "Belarusian",
    ["bg"] = "Bulgarian",
    ["bn"] = "Bengali",
    ["bo"] = "Tibetan",
    ["br"] = "Breton",
    ["bs"] = "Bosnian",
    ["ca"] = "Catalan",
    ["ch"] = "Chamorro",
    ["co"] = "Corsican",
    ["cs"] = "Czech",
    ["cy"] = "Welsh",
    ["da"] = "Danish",
    ["de"] = "German",
    ["dv"] = "Divehi",
    ["dz"] = "Dzongkha",
    ["el"] = "Greek",
    ["en"] = "English",
    ["es"] = "Spanish",
    ["et"] = "Estonian",
    ["eu"] = "Basque",
    ["fa"] = "Persian",
    ["fi"] = "Finnish",
    ["fr"] = "French",
    ["he"] = "Hebrew",
    ["hi"] = "Hindi",
    ["id"] = "Indonesian",
    ["it"] = "Italian",
    ["ja"] = "Japanese",
    ["jv"] = "Javanese",
    ["ko"] = "Korean",
    ["mr"] = "Marathi",
    ["ms"] = "Malay",
    ["nl"] = "Dutch",
    ["pl"] = "Polish",
    ["pt"] = "Portuguese",
    ["ru"] = "Russian",
    ["sv"] = "Swedish",
    ["ta"] = "Tamil",
    ["te"] = "Telugu",
    ["th"] = "Thai",
    ["tr"] = "Turkish",
    ["uk"] = "Ukrainian",
    ["ur"] = "Urdu",
    ["vi"] = "Vietnamese",
    ["zh"] = "Chinese",
    ["zu"] = "Zulu"
};
}
