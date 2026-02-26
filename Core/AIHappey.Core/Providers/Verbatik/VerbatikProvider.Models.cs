using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Verbatik;

public partial class VerbatikProvider
{
    private const string VerbatikTtsModelPrefix = "tts/";

    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        ApplyAuthHeader();

        var voices = await GetVoicesAsync(cancellationToken);
        return BuildDynamicVoiceModels(voices);
    }

    private async Task<IReadOnlyList<VerbatikVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync("api/v1/voices", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return ParseVoices(doc.RootElement);
    }

    private static IReadOnlyList<VerbatikVoice> ParseVoices(JsonElement root)
    {
        JsonElement array = default;

        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (!TryGetPropertyIgnoreCase(root, "data", out array)
                && !TryGetPropertyIgnoreCase(root, "items", out array)
                && !TryGetPropertyIgnoreCase(root, "voices", out array)
                && !TryGetPropertyIgnoreCase(root, "results", out array))
                return [];
        }
        else
        {
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<VerbatikVoice>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = ReadString(item, "id")
                ?? ReadString(item, "voice_id")
                ?? ReadString(item, "voiceId");

            if (string.IsNullOrWhiteSpace(id))
                continue;

            voices.Add(new VerbatikVoice
            {
                Id = id.Trim(),
                Name = ReadString(item, "name"),
                Gender = ReadString(item, "gender"),
                LanguageCode = ReadString(item, "language_code") ?? ReadString(item, "languageCode"),
                LanguageName = ReadString(item, "language_name") ?? ReadString(item, "languageName")
            });
        }

        return [.. voices
            .Where(IsValidVoice)
            .GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private IEnumerable<Model> BuildDynamicVoiceModels(IEnumerable<VerbatikVoice> voices)
        => voices
            .Where(IsValidVoice)
            .Select(v => new Model
            {
                Id = $"{VerbatikTtsModelPrefix}{v.Id}".ToModelId(GetIdentifier()),
                OwnedBy = ProviderName,
                Type = "speech",
                Name = BuildVoiceDisplayName(v),
                Description = $"{ProviderName} TTS voice {v.Id}.",
                Tags = BuildVoiceTags(v)
            });

    private static IEnumerable<string> BuildVoiceTags(VerbatikVoice voice)
    {
        var tags = new List<string>();

        if (!string.IsNullOrWhiteSpace(voice.LanguageCode))
            tags.Add($"language:{voice.LanguageCode}");

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"gender:{voice.Gender}");

        return tags;
    }

    private static bool IsValidVoice(VerbatikVoice voice)
        => !string.IsNullOrWhiteSpace(voice.Id);

    private static string BuildVoiceDisplayName(VerbatikVoice voice)
    {
        var name = string.IsNullOrWhiteSpace(voice.Name) ? voice.Id : voice.Name.Trim();
        var gender = string.IsNullOrWhiteSpace(voice.Gender) ? "Unknown" : voice.Gender.Trim();
        var languageName = string.IsNullOrWhiteSpace(voice.LanguageName)
            ? (string.IsNullOrWhiteSpace(voice.LanguageCode) ? "Unknown Language" : voice.LanguageCode.Trim())
            : voice.LanguageName.Trim();

        return $"{name} ({gender}, {languageName})";
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

    private sealed class VerbatikVoice
    {
        public string Id { get; set; } = null!;

        public string? Name { get; set; }

        public string? Gender { get; set; }

        public string? LanguageCode { get; set; }

        public string? LanguageName { get; set; }
    }
}

