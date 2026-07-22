using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.LelapaAI;

public partial class LelapaAIProvider
{
    private const string VulavulaModel = "vulavula";
    private const string SourceLanguageKey = "source_lang";
    private const string TargetLanguageKey = "target_lang";

    private static readonly string[] TranscriptionLanguageCodes =
    [
        "afr",
        "zul",
        "sot",
        "eng",
        "fra",
        "cs-zul"
    ];

    private static readonly string[] TranslationLanguageCodes =
    [
        "eng_Latn",
        "zul_Latn",
        "xho_Latn",
        "afr_Latn",
        "nso_Latn",
        "sot_Latn",
        "ssw_Latn",
        "tso_Latn",
        "tsn_Latn",
        "swh_Latn"
    ];

    private static string CleanModelId(string modelId, string providerId)
    {
        var model = modelId.Trim();
        var providerPrefix = providerId + "/";

        if (model.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            model = model[providerPrefix.Length..];

        return model.Trim('/');
    }

    private static bool IsCleanVulavulaModel(string? modelId, string providerId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        return string.Equals(
            CleanModelId(modelId, providerId),
            VulavulaModel,
            StringComparison.OrdinalIgnoreCase);
    }

    private string BuildTranslationModelId(string sourceLanguage, string targetLanguage)
        => $"{GetIdentifier()}/{VulavulaModel}/{sourceLanguage}/{targetLanguage}";

    internal static bool TryParseTranslationModelLanguages(
        string? modelId,
        string providerId,
        out string sourceLanguage,
        out string targetLanguage)
    {
        sourceLanguage = string.Empty;
        targetLanguage = string.Empty;

        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var model = CleanModelId(modelId, providerId);
        var parts = model.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 3
            || !string.Equals(parts[0], VulavulaModel, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsSupportedTranslationLanguage(parts[1]) || !IsSupportedTranslationLanguage(parts[2]))
            return false;

        if (string.Equals(parts[1], parts[2], StringComparison.OrdinalIgnoreCase))
            return false;

        sourceLanguage = parts[1];
        targetLanguage = parts[2];
        return true;
    }

    private static (string SourceLanguage, string TargetLanguage) ResolveTranslationLanguages(
        string? modelId,
        string providerId,
        Dictionary<string, object?>? metadata)
    {
        if (TryParseTranslationModelLanguages(modelId, providerId, out var sourceFromModel, out var targetFromModel))
            return (sourceFromModel, targetFromModel);

        if (!IsCleanVulavulaModel(modelId, providerId))
            throw new ArgumentException($"LelapaAI translation model must be '{providerId}/{VulavulaModel}' or '{providerId}/{VulavulaModel}/{{source_lang}}/{{target_lang}}'. Got '{modelId}'.", nameof(modelId));

        var source = GetProviderMetadataString(metadata, providerId, SourceLanguageKey);
        var target = GetProviderMetadataString(metadata, providerId, TargetLanguageKey);

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            throw new ArgumentException($"LelapaAI clean translation model '{providerId}/{VulavulaModel}' requires provider metadata fields '{SourceLanguageKey}' and '{TargetLanguageKey}'.", nameof(metadata));

        if (!IsSupportedTranslationLanguage(source))
            throw new ArgumentException($"Unsupported LelapaAI source language '{source}'.", nameof(metadata));

        if (!IsSupportedTranslationLanguage(target))
            throw new ArgumentException($"Unsupported LelapaAI target language '{target}'.", nameof(metadata));

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("LelapaAI source and target language must be different.", nameof(metadata));

        return (source, target);
    }

    private static string? GetProviderMetadataString(
        Dictionary<string, object?>? metadata,
        string providerId,
        string key)
    {
        if (metadata is null)
            return null;

        if (TryGetString(metadata, key, out var direct))
            return direct;

        if (!metadata.TryGetValue(providerId, out var providerMetadata) || providerMetadata is null)
            return null;

        if (providerMetadata is JsonElement providerElement)
            return TryGetString(providerElement, key, out var fromJson) ? fromJson : null;

        var element = JsonSerializer.SerializeToElement(providerMetadata, JsonSerializerOptions.Web);
        return TryGetString(element, key, out var serialized) ? serialized : null;
    }

    private static bool TryGetString(Dictionary<string, object?> metadata, string key, out string? value)
    {
        value = null;

        if (!metadata.TryGetValue(key, out var raw) || raw is null)
            return false;

        switch (raw)
        {
            case string s:
                value = s;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } json:
                value = json.GetString();
                return true;
            default:
                value = raw.ToString();
                return !string.IsNullOrWhiteSpace(value);
        }
    }

    private static bool TryGetString(JsonElement element, string key, out string? value)
    {
        value = null;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString();
                return true;
            }

            value = property.Value.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    private static bool IsSupportedTranslationLanguage(string language)
        => TranslationLanguageCodes.Contains(language, StringComparer.OrdinalIgnoreCase);

    private static bool IsSupportedTranscriptionLanguage(string language)
        => TranscriptionLanguageCodes.Contains(language, StringComparer.OrdinalIgnoreCase);

    private IEnumerable<Model> BuildTranslationCombinationModels()
    {
        foreach (var sourceLanguage in TranslationLanguageCodes)
        {
            foreach (var targetLanguage in TranslationLanguageCodes)
            {
                if (string.Equals(sourceLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return new Model
                {
                    Id = BuildTranslationModelId(sourceLanguage, targetLanguage),
                    Object = "model",
                    OwnedBy = "LelapaAI",
                    Name = $"Vulavula Translate {sourceLanguage} to {targetLanguage}",
                    Type = "language",
                    Tags = ["translate", 
                        sourceLanguage.NormalizeLanguageCode(), 
                        targetLanguage.NormalizeLanguageCode()]
                };
            }
        }
    }
}
