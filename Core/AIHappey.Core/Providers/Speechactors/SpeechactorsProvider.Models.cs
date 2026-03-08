using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Speechactors;

public partial class SpeechactorsProvider
{
    private const string SpeechactorsTtsModelPrefix = "tts/";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        ApplyAuthHeader();

        var voices = await GetVoicesAsync(cancellationToken);
        var languages = await GetLanguagesAsync(cancellationToken);

        return [.. BuildDynamicVoiceModels(voices, languages)
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private async Task<IReadOnlyList<SpeechactorsVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync("v1/voices", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{nameof(Speechactors)} voices list failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return ParseVoices(doc.RootElement);
    }

    private async Task<IReadOnlyDictionary<string, string>> GetLanguagesAsync(CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync("v1/languages", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{nameof(Speechactors)} languages list failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return ParseLanguages(doc.RootElement);
    }

    private static IReadOnlyList<SpeechactorsVoice> ParseVoices(JsonElement root)
    {
        if (!TryExtractArray(root, out var array))
            return [];

        var voices = new List<SpeechactorsVoice>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var vid = ReadString(item, "vid")?.Trim();
            if (string.IsNullOrWhiteSpace(vid))
                continue;

            voices.Add(new SpeechactorsVoice
            {
                Vid = vid,
                Name = ReadString(item, "name"),
                Gender = ReadString(item, "gender"),
                Locale = ReadString(item, "locale"),
                LocaleName = ReadString(item, "locale_name") ?? ReadString(item, "localeName"),
                Style = ReadString(item, "style")
            });
        }

        return [.. voices
            .Where(v => !string.IsNullOrWhiteSpace(v.Vid))
            .GroupBy(v => v.Vid, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private static IReadOnlyDictionary<string, string> ParseLanguages(JsonElement root)
    {
        if (!TryExtractArray(root, out var array))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var code = ReadString(item, "code")?.Trim();
            var language = ReadString(item, "language")?.Trim();

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(language))
                continue;

            dict[code] = language;
        }

        return dict;
    }

    private IEnumerable<Model> BuildDynamicVoiceModels(
        IReadOnlyList<SpeechactorsVoice> voices,
        IReadOnlyDictionary<string, string> languages)
    {
        foreach (var voice in voices)
        {
            var locale = NormalizeLocale(voice.Locale, voice.Vid);
            if (string.IsNullOrWhiteSpace(locale))
                continue;

            var languageDisplay = ResolveLanguageDisplay(locale, voice, languages);
            var displayName = string.IsNullOrWhiteSpace(voice.Name) ? voice.Vid : voice.Name.Trim();
            var genderDisplay = string.IsNullOrWhiteSpace(voice.Gender) ? "Unknown" : voice.Gender.Trim();
            var styles = ParseStyles(voice.Style);

            if (styles.Count == 0)
            {
                yield return BuildVoiceModel(voice, locale, languageDisplay, displayName, genderDisplay, style: null);
                continue;
            }

            foreach (var style in styles)
            {
                yield return BuildVoiceModel(voice, locale, languageDisplay, displayName, genderDisplay, style);
            }
        }
    }

    private Model BuildVoiceModel(
        SpeechactorsVoice voice,
        string locale,
        string languageDisplay,
        string displayName,
        string genderDisplay,
        string? style)
    {
        var normalizedBaseId = $"{SpeechactorsTtsModelPrefix}{voice.Vid}/{locale}";
        var normalizedId = string.IsNullOrWhiteSpace(style)
            ? normalizedBaseId
            : $"{normalizedBaseId}/style/{style.Trim()}";

        var hasStyle = !string.IsNullOrWhiteSpace(style);
        var styleSuffix = hasStyle ? $", style: {style!.Trim()}" : string.Empty;

        return new Model
        {
            Id = normalizedId.ToModelId(GetIdentifier()),
            OwnedBy = nameof(Speechactors),
            Type = "speech",
            Name = $"{displayName} ({genderDisplay}, {languageDisplay}{styleSuffix})",
            Description = hasStyle
                ? $"{nameof(Speechactors)} TTS voice '{displayName}' (vid={voice.Vid}, locale={locale}) with style='{style!.Trim()}'."
                : $"{nameof(Speechactors)} TTS voice '{displayName}' (vid={voice.Vid}, locale={locale}) with no style parameter required.",
            Tags = BuildVoiceTags(voice, locale, style)
        };
    }

    private static IEnumerable<string> BuildVoiceTags(SpeechactorsVoice voice, string locale, string? style)
    {
        var tags = new List<string>
        {
            $"voice:{voice.Vid}",
            $"locale:{locale}"
        };

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"gender:{voice.Gender.Trim()}");

        if (!string.IsNullOrWhiteSpace(style))
            tags.Add($"style:{style.Trim()}");

        return tags;
    }

    private static string ResolveLanguageDisplay(
        string locale,
        SpeechactorsVoice voice,
        IReadOnlyDictionary<string, string> languages)
    {
        if (languages.TryGetValue(locale, out var language) && !string.IsNullOrWhiteSpace(language))
            return language.Trim();

        if (!string.IsNullOrWhiteSpace(voice.LocaleName))
            return voice.LocaleName.Trim();

        return locale;
    }

    private static string? NormalizeLocale(string? locale, string? vid)
    {
        if (!string.IsNullOrWhiteSpace(locale))
            return locale.Trim();

        if (string.IsNullOrWhiteSpace(vid))
            return null;

        var trimmedVid = vid.Trim();
        var lastSeparator = trimmedVid.LastIndexOf('-');
        if (lastSeparator <= 0)
            return null;

        return trimmedVid[..lastSeparator];
    }

    private static IReadOnlyList<string> ParseStyles(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return [.. raw
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static bool TryExtractArray(JsonElement root, out JsonElement array)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(root, "data", out array) && array.ValueKind == JsonValueKind.Array)
                return true;

            if (TryGetPropertyIgnoreCase(root, "items", out array) && array.ValueKind == JsonValueKind.Array)
                return true;

            if (TryGetPropertyIgnoreCase(root, "voices", out array) && array.ValueKind == JsonValueKind.Array)
                return true;

            if (TryGetPropertyIgnoreCase(root, "languages", out array) && array.ValueKind == JsonValueKind.Array)
                return true;
        }

        array = default;
        return false;
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();

        if (el.ValueKind == JsonValueKind.Number)
            return el.GetRawText();

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed class SpeechactorsVoice
    {
        public string Vid { get; set; } = null!;

        public string? Name { get; set; }

        public string? Gender { get; set; }

        public string? Locale { get; set; }

        public string? LocaleName { get; set; }

        public string? Style { get; set; }
    }
}
