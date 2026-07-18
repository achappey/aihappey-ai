
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Core.AI;


namespace AIHappey.Core.Providers.MurfAI;

public sealed partial class MurfAIProvider
{
    private static readonly string[] MurfSpeechModelVersions = ["gen2", "falcon-2"];

    public async Task<IEnumerable<Model>> ListModels(
        CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        var models = (await this.ListModels(key)).ToList();

        if (!string.IsNullOrWhiteSpace(key))
        {
            var voices = await GetVoicesAsync(key, cancellationToken);
            models.AddRange(BuildVoiceShortcutModels(voices));
        }

        return models
            .Concat(CreateMurfTranslationModels())
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
    }

    private async Task<IReadOnlyList<MurfVoice>> GetVoicesAsync(string apiKey, CancellationToken cancellationToken)
    {
        var cacheKey = $"{this.GetCacheKey(apiKey)}:voices";

        return await _memoryCache.GetOrCreateAsync<IReadOnlyList<MurfVoice>>(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                var voices = new List<MurfVoice>();
                foreach (var modelVersion in MurfSpeechModelVersions)
                    voices.AddRange(await ListVoicesForModelAsync(modelVersion, ct));

                return [.. voices
                    .Where(IsValidVoice)
                    .GroupBy(voice => $"{voice.ModelVersion}/{voice.VoiceId}", StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())];
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<MurfVoice>> ListVoicesForModelAsync(string modelVersion, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(
            $"v1/speech/voices?model={Uri.EscapeDataString(modelVersion)}",
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MurfAI voices list failed for model '{modelVersion}' ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        return ParseVoices(document.RootElement, modelVersion);
    }

    private IEnumerable<Model> BuildVoiceShortcutModels(IEnumerable<MurfVoice> voices)
        => voices
            .Where(IsValidVoice)
            .Select(voice => new Model
            {
                Id = $"{voice.ModelVersion}/{voice.VoiceId}".ToModelId(GetIdentifier()),
                OwnedBy = "MurfAI",
                Type = "speech",
                Name = BuildVoiceDisplayName(voice),
                Description = BuildVoiceDescription(voice),
                Tags = BuildVoiceTags(voice)
            });

    private static IReadOnlyList<MurfVoice> ParseVoices(JsonElement root, string modelVersion)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<MurfVoice>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var voiceId = ReadString(item, "voiceId");
            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            var supportedLocales = ReadSupportedLocales(item);
            voices.Add(new MurfVoice
            {
                ModelVersion = modelVersion,
                VoiceId = voiceId.Trim(),
                DisplayName = ReadString(item, "displayName"),
                Description = ReadString(item, "description"),
                Gender = ReadString(item, "gender"),
                Locale = ReadString(item, "locale"),
                SupportedLocales = supportedLocales,
                AvailableStyles = ReadStringArray(item, "availableStyles")
            });
        }

        return voices;
    }

    private static IReadOnlyDictionary<string, MurfSupportedLocale> ReadSupportedLocales(JsonElement voice)
    {
        if (!TryGetPropertyIgnoreCase(voice, "supportedLocales", out var locales)
            || locales.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, MurfSupportedLocale>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, MurfSupportedLocale>(StringComparer.OrdinalIgnoreCase);
        foreach (var locale in locales.EnumerateObject())
        {
            if (locale.Value.ValueKind != JsonValueKind.Object)
                continue;

            result[locale.Name] = new MurfSupportedLocale(
                ReadString(locale.Value, "detail"),
                ReadStringArray(locale.Value, "availableStyles"));
        }

        return result;
    }

    private static string BuildVoiceDisplayName(MurfVoice voice)
    {
        var name = string.IsNullOrWhiteSpace(voice.DisplayName) ? voice.VoiceId : voice.DisplayName.Trim();
        return $"{voice.ModelVersion} / {name}";
    }

    private static string BuildVoiceDescription(MurfVoice voice)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(voice.Description)) details.Add(voice.Description.Trim());
        if (!string.IsNullOrWhiteSpace(voice.Gender)) details.Add(voice.Gender.Trim());
        if (!string.IsNullOrWhiteSpace(voice.Locale)) details.Add(voice.Locale.Trim());
        if (voice.AvailableStyles.Count > 0) details.Add($"Styles: {string.Join(", ", voice.AvailableStyles)}");
        return string.Join(". ", details);
    }

    private static IEnumerable<string> BuildVoiceTags(MurfVoice voice)
    {
        var tags = new List<string> { "voice", voice.ModelVersion };
        if (!string.IsNullOrWhiteSpace(voice.Gender)) tags.Add(voice.Gender.Trim().ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(voice.Locale)) tags.Add(voice.Locale.Trim().NormalizeLanguageCode());
        tags.AddRange(voice.SupportedLocales.Keys.Select(locale => locale.NormalizeLanguageCode()));
        tags.AddRange(voice.AvailableStyles.Select(style => style.Trim().ToLowerInvariant()));
        return tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsValidVoice(MurfVoice voice)
        => !string.IsNullOrWhiteSpace(voice.ModelVersion) && !string.IsNullOrWhiteSpace(voice.VoiceId);

    private static string? ReadString(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return [.. value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())];
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed class MurfVoice
    {
        public string ModelVersion { get; init; } = null!;
        public string VoiceId { get; init; } = null!;
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
        public string? Gender { get; init; }
        public string? Locale { get; init; }
        public IReadOnlyDictionary<string, MurfSupportedLocale> SupportedLocales { get; init; } = new Dictionary<string, MurfSupportedLocale>();
        public IReadOnlyList<string> AvailableStyles { get; init; } = [];
    }

    private sealed record MurfSupportedLocale(string? Detail, IReadOnlyList<string> AvailableStyles);

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

