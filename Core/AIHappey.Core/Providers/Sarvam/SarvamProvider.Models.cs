using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Sarvam;

public sealed partial class SarvamProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);


        ApplyAuthHeader();

        List<Model> models = [.. await this.ListModels(_keyResolver.Resolve(GetIdentifier()))];

        // ─────────────────────────────────────────────────────────────
        // mayura:v1 → target-only translations
        // ─────────────────────────────────────────────────────────────
        foreach (var (targetCode, targetName) in MayuraLanguages)
        {
            models.Add(new Model
            {
                Id = $"mayura:v1/translate-to/{targetCode}".ToModelId(GetIdentifier()),
                Name = $"mayura Translate to {targetName}",
                OwnedBy = nameof(Sarvam),
                Type = "language",
                Description = $"Translate text into {targetName} using mayura:v1."
            });
        }

        // ─────────────────────────────────────────────────────────────
        // sarvam-translate:v1 → full source × target matrix
        // ─────────────────────────────────────────────────────────────
        foreach (var (sourceCode, sourceName) in SarvamLanguages)
        {
            foreach (var (targetCode, targetName) in SarvamLanguages)
            {
                // Skip identity pairs (en-IN → en-IN, etc.)
                if (sourceCode == targetCode)
                    continue;

                models.Add(new Model
                {
                    Id = $"sarvam-translate:v1/translate/{sourceCode}/to/{targetCode}"
                        .ToModelId(GetIdentifier()),

                    Name = $"sarvam Translate {sourceName} to {targetName}",
                    OwnedBy = nameof(Sarvam),
                    Type = "language",
                    Description =
                        $"Translate text from {sourceName} ({sourceCode}) to {targetName} ({targetCode}) " +
                        "using sarvam-translate:v1."
                });
            }
        }
        return models;

    }

    public static readonly IReadOnlyDictionary<string, string> MayuraLanguages =
        new Dictionary<string, string>
        {
            ["bn-IN"] = "Bengali",
            ["en-IN"] = "English",
            ["gu-IN"] = "Gujarati",
            ["hi-IN"] = "Hindi",
            ["kn-IN"] = "Kannada",
            ["ml-IN"] = "Malayalam",
            ["mr-IN"] = "Marathi",
            ["od-IN"] = "Odia",
            ["pa-IN"] = "Punjabi",
            ["ta-IN"] = "Tamil",
            ["te-IN"] = "Telugu"
        };

    public static readonly IReadOnlyDictionary<string, string> SarvamLanguages =
        new Dictionary<string, string>(
            MayuraLanguages // copy base languages
        )
        {
            // Extra languages supported by sarvam-translate:v1
            ["as-IN"] = "Assamese",
            ["brx-IN"] = "Bodo",
            ["doi-IN"] = "Dogri",
            ["kok-IN"] = "Konkani",
            ["ks-IN"] = "Kashmiri",
            ["mai-IN"] = "Maithili",
            ["mni-IN"] = "Manipuri",
            ["ne-IN"] = "Nepali",
            ["sa-IN"] = "Sanskrit",
            ["sat-IN"] = "Santali",
            ["sd-IN"] = "Sindhi",
            ["ur-IN"] = "Urdu"
        };


}

