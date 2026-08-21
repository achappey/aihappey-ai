using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.Sarvam;

public sealed partial class SarvamProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        List<Model> models = [.. await this.ListModels(_keyResolver.Resolve(GetIdentifier()))];

        // The v2 router inventory is deployment-specific. Merge the live list with
        // the local catalog, but keep the catalog usable when beta access is absent.
        try
        {
            ApplyChatAuthHeaders();
            using var response = await _client.GetAsync("v2/models", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (!item.TryGetProperty("id", out var idElement)) continue;
                        var id = idElement.GetString();
                        if (string.IsNullOrWhiteSpace(id)) continue;

                        models.Add(new Model
                        {
                            Id = id.ToModelId(GetIdentifier()),
                            Name = id,
                            OwnedBy = item.TryGetProperty("owned_by", out var owner) ? owner.GetString() ?? nameof(Sarvam) : nameof(Sarvam),
                            Created = item.TryGetProperty("created", out var created) && created.TryGetInt64(out var value) ? value : null,
                            Type = "language",
                            Description = "Sarvam v2 router model."
                        });
                    }
                }
            }
        }
        catch (HttpRequestException)
        {
            // Static catalog remains the fallback for unavailable/beta-only v2 APIs.
        }

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

        return models
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .WithPricing(GetIdentifier());
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

