using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.UVoiceAI;

public partial class UVoiceAIProvider
{
    private const string UVoiceModelPrefix = "tts/";
    private const string VoicesEndpoint = "https://uvoice.app/?getVoice=true&filter=All&source=API-DOCS";

    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        var voices = await GetVoicesAsync(cancellationToken);

        return [.. voices
            .Select(v => new Model
            {
                Id = BuildModelId(v.VoiceId),
                OwnedBy = ProviderName,
                Type = "speech",
                Name = BuildVoiceDisplayName(v),
                Description = $"{ProviderName} TTS voice {v.VoiceId}.",
                Tags = BuildVoiceTags(v)
            })
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private async Task<IReadOnlyList<UVoiceVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        var client = _factory.CreateClient();
        using var resp = await client.GetAsync(VoicesEndpoint, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return ParseVoices(doc.RootElement);
    }

    private static IReadOnlyList<UVoiceVoice> ParseVoices(JsonElement root)
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

        var voices = new List<UVoiceVoice>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var voiceId = ReadString(item, "voiceID")
                ?? ReadString(item, "voiceId")
                ?? ReadString(item, "voice_id");

            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            var displayName = ReadString(item, "displayName") ?? ReadString(item, "name");
            var langCode = ReadString(item, "langCode") ?? ReadString(item, "lang");
            var gender = ReadString(item, "gender");
            var voiceType = ReadString(item, "type");

            if (TryGetPropertyIgnoreCase(item, "supportedLangs", out var supportedLangs)
                && supportedLangs.ValueKind == JsonValueKind.Array)
            {
                foreach (var lang in supportedLangs.EnumerateArray())
                {
                    if (lang.ValueKind != JsonValueKind.Object)
                        continue;

                    var mappedVoiceId = ReadString(lang, "voiceID")
                        ?? ReadString(lang, "voiceId")
                        ?? ReadString(lang, "voice_id")
                        ?? voiceId;

                    if (string.IsNullOrWhiteSpace(mappedVoiceId))
                        continue;

                    voices.Add(new UVoiceVoice
                    {
                        VoiceId = mappedVoiceId.Trim(),
                        DisplayName = (ReadString(lang, "displayName") ?? displayName)?.Trim(),
                        LanguageCode = (ReadString(lang, "langID") ?? ReadString(lang, "langCode") ?? langCode)?.Trim(),
                        Gender = gender?.Trim(),
                        VoiceType = voiceType?.Trim()
                    });
                }
            }
            else
            {
                voices.Add(new UVoiceVoice
                {
                    VoiceId = voiceId.Trim(),
                    DisplayName = displayName?.Trim(),
                    LanguageCode = langCode?.Trim(),
                    Gender = gender?.Trim(),
                    VoiceType = voiceType?.Trim()
                });
            }
        }

        return [.. voices
            .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId))
            .GroupBy(v => v.VoiceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private string BuildModelId(string voiceId)
        => $"{UVoiceModelPrefix}{voiceId}".ToModelId(GetIdentifier());

    private static string BuildVoiceDisplayName(UVoiceVoice voice)
    {
        var display = string.IsNullOrWhiteSpace(voice.DisplayName)
            ? voice.VoiceId
            : voice.DisplayName.Trim();

        if (string.IsNullOrWhiteSpace(voice.LanguageCode))
            return display;

        return $"{display} ({voice.LanguageCode.Trim().ToUpperInvariant()})";
    }

    private static IEnumerable<string> BuildVoiceTags(UVoiceVoice voice)
    {
        var tags = new List<string>
        {
            $"voice:{voice.VoiceId}"
        };

        if (!string.IsNullOrWhiteSpace(voice.LanguageCode))
            tags.Add($"lang:{voice.LanguageCode.Trim().ToLowerInvariant()}");

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"gender:{voice.Gender.Trim().ToLowerInvariant()}");

        if (!string.IsNullOrWhiteSpace(voice.VoiceType))
            tags.Add($"tier:{voice.VoiceType.Trim().ToLowerInvariant()}");

        return tags;
    }

    private static string? ReadString(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();

        if (el.ValueKind == JsonValueKind.Number)
            return el.GetRawText();

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed class UVoiceVoice
    {
        public string VoiceId { get; set; } = null!;

        public string? DisplayName { get; set; }

        public string? LanguageCode { get; set; }

        public string? Gender { get; set; }

        public string? VoiceType { get; set; }
    }
}

