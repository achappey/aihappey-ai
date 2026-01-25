using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Sarvam;

public sealed partial class SarvamProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var models = await Task.FromResult<List<Model>>(
        [
            new Model
                {
                    Id = "sarvam-m".ToModelId(GetIdentifier()),
                    Name = "sarvam-m",
                    OwnedBy = nameof(Sarvam),
                    Description = "sarvam-m is a multilingual, hybrid-reasoning, text-only language model built on Mistral-Small. This post-trained version delivers exceptional improvements over the base model. Performance gains are even more impressive at the intersection of Indian languages and mathematics, with an outstanding +86% improvement in romanized Indian language GSM-8K benchmarks.",
                    Type = "language",
                    Pricing = new ModelPricing() {
                        Input = 0,
                        Output = 0
                    },
                    Created = new DateTimeOffset(2025, 5, 23, 0, 0, 0, TimeSpan.Zero)
                        .ToUnixTimeSeconds()
                },
                new Model
                {
                    Id = "saarika:v2.5".ToModelId(GetIdentifier()),
                    Name = "saarika:v2.5",
                    OwnedBy = nameof(Sarvam),
                    Description = "The Saarika model can be used for converting speech to text across different scenarios. It supports basic transcription, code-mixed speech, and automatic language detection for Indian languages.",
                    Type = "transcription",
                    Created = new DateTimeOffset(2025, 5, 23, 0, 0, 0, TimeSpan.Zero)
                        .ToUnixTimeSeconds()

                },
                new Model
                {
                    Id = "bulbul:v2".ToModelId(GetIdentifier()),
                    Name = "bulbul:v2",
                    OwnedBy = nameof(Sarvam),
                    Type = "speech",
                    Description = "Bulbul-v2 is our flagship text-to-speech model, specifically designed for Indian languages and accents. It excels in natural-sounding speech synthesis with human-like prosody, multiple voice personalities, and comprehensive support for multiple Indian languages",
                    Created = new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero)
                        .ToUnixTimeSeconds()

                }
        ]);

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

