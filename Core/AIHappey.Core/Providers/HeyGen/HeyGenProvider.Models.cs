using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.HeyGen;

public partial class HeyGenProvider
{
    private const string HeyGenVoiceModelPrefix = "";

    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        ApplyAuthHeader();

        var voices = new List<HeyGenVoice>();
        voices.AddRange(await GetVoicesByTypeAsync("public", cancellationToken));
        voices.AddRange(await GetVoicesByTypeAsync("private", cancellationToken));

        return BuildDynamicVoiceModels(voices);
    }

    private async Task<IReadOnlyList<HeyGenVoice>> GetVoicesByTypeAsync(string type, CancellationToken cancellationToken)
    {
        const int limit = 100;
        var token = default(string);
        var all = new List<HeyGenVoice>();

        for (var page = 0; page < 1000; page++)
        {
            var path = $"v1/audio/voices?type={Uri.EscapeDataString(type)}&limit={limit}";
            if (!string.IsNullOrWhiteSpace(token))
                path += $"&token={Uri.EscapeDataString(token)}";

            using var resp = await _client.GetAsync(path, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}) for type '{type}': {body}");

            using var doc = JsonDocument.Parse(body);

            var pageVoices = ParseVoices(doc.RootElement, type);
            if (pageVoices.Count > 0)
                all.AddRange(pageVoices);

            token = TryFindNextToken(doc.RootElement);
            if (string.IsNullOrWhiteSpace(token))
                break;
        }

        return [..
            all
            .Where(IsValidVoice)
            .GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => MergeVoiceGroup(g))];
    }

    private static IReadOnlyList<HeyGenVoice> ParseVoices(JsonElement root, string catalogType)
    {
        JsonElement array = default;

        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (!TryGetPropertyIgnoreCase(root, "data", out var data))
                return [];

            if (data.ValueKind == JsonValueKind.Array)
                array = data;
            else if (data.ValueKind == JsonValueKind.Object)
            {
                if (!TryGetPropertyIgnoreCase(data, "voices", out array)
                    && !TryGetPropertyIgnoreCase(data, "items", out array)
                    && !TryGetPropertyIgnoreCase(data, "results", out array)
                    && !TryGetPropertyIgnoreCase(data, "data", out array))
                    return [];
            }
            else
            {
                return [];
            }
        }
        else
        {
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<HeyGenVoice>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = ReadString(item, "voice_id")
                ?? ReadString(item, "voiceId")
                ?? ReadString(item, "id");

            if (string.IsNullOrWhiteSpace(id))
                continue;

            voices.Add(new HeyGenVoice
            {
                Id = id.Trim(),
                Name = ReadString(item, "name")
                    ?? ReadString(item, "display_name")
                    ?? ReadString(item, "displayName"),
                Language = ReadString(item, "language")
                    ?? ReadString(item, "locale"),
                Gender = ReadString(item, "gender"),
                CatalogType = catalogType
            });
        }

        return voices;
    }

    private static string? TryFindNextToken(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (TryGetPropertyIgnoreCase(root, "data", out var data))
        {
            var token = ReadString(data, "token")
                ?? ReadString(data, "next_token")
                ?? ReadString(data, "nextToken")
                ?? ReadString(data, "cursor");

            if (!string.IsNullOrWhiteSpace(token))
                return token;
        }

        return ReadString(root, "token")
            ?? ReadString(root, "next_token")
            ?? ReadString(root, "nextToken")
            ?? ReadString(root, "cursor");
    }

    private static HeyGenVoice MergeVoiceGroup(IGrouping<string, HeyGenVoice> group)
    {
        var voice = group.First();

        voice.Name = FirstNonWhiteSpace(group.Select(a => a.Name)) ?? voice.Id;
        voice.Language = FirstNonWhiteSpace(group.Select(a => a.Language));
        voice.Gender = FirstNonWhiteSpace(group.Select(a => a.Gender));

        var hasPublic = group.Any(a => string.Equals(a.CatalogType, "public", StringComparison.OrdinalIgnoreCase));
        var hasPrivate = group.Any(a => string.Equals(a.CatalogType, "private", StringComparison.OrdinalIgnoreCase));
        voice.CatalogType = hasPublic && hasPrivate
            ? "public+private"
            : hasPrivate
                ? "private"
                : "public";

        return voice;
    }

    private IEnumerable<Model> BuildDynamicVoiceModels(IEnumerable<HeyGenVoice> voices)
        => voices
            .Where(IsValidVoice)
            .Select(v => new Model
            {
                Id = $"{HeyGenVoiceModelPrefix}{v.Id}".ToModelId(GetIdentifier()),
                OwnedBy = ProviderName,
                Type = "speech",
                Name = BuildVoiceDisplayName(v),
                Description = BuildVoiceDescription(v),
                Tags = BuildVoiceTags(v)
            });

    private static IEnumerable<string> BuildVoiceTags(HeyGenVoice voice)
    {
        var tags = new List<string>();

        if (!string.IsNullOrWhiteSpace(voice.Language))
            tags.Add($"language:{voice.Language}");

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"gender:{voice.Gender}");

        if (!string.IsNullOrWhiteSpace(voice.CatalogType))
            tags.Add($"catalog:{voice.CatalogType}");

        return tags;
    }

    private static string BuildVoiceDisplayName(HeyGenVoice voice)
    {
        var name = string.IsNullOrWhiteSpace(voice.Name) ? voice.Id : voice.Name.Trim();
        var language = string.IsNullOrWhiteSpace(voice.Language) ? "Unknown Language" : voice.Language.Trim();
        var gender = string.IsNullOrWhiteSpace(voice.Gender) ? "Unknown" : voice.Gender.Trim();

        return $"{name} ({language}, {gender})";
    }

    private static string BuildVoiceDescription(HeyGenVoice voice)
    {
        var name = string.IsNullOrWhiteSpace(voice.Name) ? voice.Id : voice.Name.Trim();
        var language = string.IsNullOrWhiteSpace(voice.Language) ? "unknown" : voice.Language.Trim();
        var gender = string.IsNullOrWhiteSpace(voice.Gender) ? "unknown" : voice.Gender.Trim();
        var catalog = string.IsNullOrWhiteSpace(voice.CatalogType) ? "unknown" : voice.CatalogType.Trim();

        return $"{ProviderName} Starfish voice '{name}' (voice_id: {voice.Id}, language: {language}, gender: {gender}, catalog: {catalog}).";
    }

    private static string? FirstNonWhiteSpace(IEnumerable<string?> values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static bool IsValidVoice(HeyGenVoice voice)
        => !string.IsNullOrWhiteSpace(voice.Id);

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
        if (obj.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

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

    private sealed class HeyGenVoice
    {
        public string Id { get; set; } = null!;
        public string? Name { get; set; }
        public string? Language { get; set; }
        public string? Gender { get; set; }
        public string? CatalogType { get; set; }
    }
}

