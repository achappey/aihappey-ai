using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Speechify;

public partial class SpeechifyProvider
{
    private const string ProviderName = nameof(Speechify);

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync<IEnumerable<Model>>(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                var speechifyModels = await GetSpeechifyModelsAsync(cancellationToken);
                var voices = await GetSpeechifyVoicesAsync(cancellationToken);

                var models = new List<Model>();
                models.AddRange(speechifyModels.Select(BuildSpeechifyModel));
                models.AddRange(BuildSpeechifyVoiceModels(speechifyModels, voices));

                return [.. models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];

            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<SpeechifyModelInfo>> GetSpeechifyModelsAsync(CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync("v1/audio/models", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} models list failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        if (!TryGetPropertyIgnoreCase(doc.RootElement, "models", out var modelsEl) || modelsEl.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<SpeechifyModelInfo>();
        foreach (var item in modelsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            models.Add(new SpeechifyModelInfo
            {
                Id = id.Trim(),
                Name = ReadString(item, "name")?.Trim(),
                IsDefault = ReadBool(item, "default"),
                IsRecommended = ReadBool(item, "recommended"),
                Description = ReadString(item, "description")?.Trim(),
                Languages = ReadStringArray(item, "languages")
            });
        }

        return [.. models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private async Task<IReadOnlyList<SpeechifyVoiceInfo>> GetSpeechifyVoicesAsync(CancellationToken cancellationToken)
    {
        const int limit = 200;
        var voices = new List<SpeechifyVoiceInfo>();
        string? cursor = null;

        while (true)
        {
            var path = string.IsNullOrWhiteSpace(cursor)
                ? $"v1/voices?limit={limit}"
                : $"v1/voices?limit={limit}&cursor={Uri.EscapeDataString(cursor)}";

            using var resp = await _client.GetAsync(path, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var pageVoices = ParseSpeechifyVoices(root);
            voices.AddRange(pageVoices);

            var hasMore = ReadBool(root, "has_more") == true;
            var nextCursor = ReadString(root, "next_cursor");

            if (!hasMore || string.IsNullOrWhiteSpace(nextCursor) || string.Equals(nextCursor, cursor, StringComparison.Ordinal))
                break;

            cursor = nextCursor;
        }

        return [.. voices
            .Where(IsValidSpeechifyVoice)
            .GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private static IReadOnlyList<SpeechifyVoiceInfo> ParseSpeechifyVoices(JsonElement root)
    {
        JsonElement voicesEl = default;
        if (root.ValueKind == JsonValueKind.Array)
        {
            voicesEl = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (!TryGetPropertyIgnoreCase(root, "voices", out voicesEl)
                && !TryGetPropertyIgnoreCase(root, "data", out voicesEl)
                && !TryGetPropertyIgnoreCase(root, "items", out voicesEl)
                && !TryGetPropertyIgnoreCase(root, "results", out voicesEl))
                return [];
        }
        else
        {
            return [];
        }

        if (voicesEl.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<SpeechifyVoiceInfo>();
        foreach (var item in voicesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = ReadString(item, "id")
                ?? ReadString(item, "voice_id")
                ?? ReadString(item, "voiceId");

            if (string.IsNullOrWhiteSpace(id))
                continue;

            voices.Add(new SpeechifyVoiceInfo
            {
                Id = id.Trim(),
                DisplayName = ReadString(item, "display_name")?.Trim()
                    ?? ReadString(item, "displayName")?.Trim()
                    ?? ReadString(item, "name")?.Trim(),
                Gender = ReadString(item, "gender")?.Trim(),
                Locale = ReadString(item, "locale")?.Trim(),
                Type = ReadString(item, "type")?.Trim(),
                PreviewAudio = ReadString(item, "preview_audio")?.Trim()
                    ?? ReadString(item, "previewAudio")?.Trim(),
                AvatarImage = ReadString(item, "avatar_image")?.Trim()
                    ?? ReadString(item, "avatarImage")?.Trim(),
                Tags = ReadStringArray(item, "tags"),
                Models = ParseSpeechifyVoiceModels(item)
            });
        }

        return voices;
    }

    private static IReadOnlyList<SpeechifyVoiceModelInfo> ParseSpeechifyVoiceModels(JsonElement voice)
    {
        if (!TryGetPropertyIgnoreCase(voice, "models", out var modelsEl) || modelsEl.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<SpeechifyVoiceModelInfo>();
        foreach (var item in modelsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var name = ReadString(item, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            models.Add(new SpeechifyVoiceModelInfo
            {
                Name = name.Trim(),
                Languages = ParseSpeechifyVoiceModelLanguages(item)
            });
        }

        return [.. models
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private static IReadOnlyList<string> ParseSpeechifyVoiceModelLanguages(JsonElement model)
    {
        if (!TryGetPropertyIgnoreCase(model, "languages", out var languagesEl) || languagesEl.ValueKind != JsonValueKind.Array)
            return [];

        var languages = new List<string>();
        foreach (var item in languagesEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var locale = item.GetString();
                if (!string.IsNullOrWhiteSpace(locale))
                    languages.Add(locale.Trim());
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var objectLocale = ReadString(item, "locale");
            if (!string.IsNullOrWhiteSpace(objectLocale))
                languages.Add(objectLocale.Trim());
        }

        return [.. languages.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private Model BuildSpeechifyModel(SpeechifyModelInfo model)
        => new()
        {
            Id = model.Id.ToModelId(GetIdentifier()),
            OwnedBy = ProviderName,
            Type = "speech",
            Name = string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name,
            Description = model.Description,
            Tags = BuildSpeechifyModelTags(model)
        };

    private IEnumerable<Model> BuildSpeechifyVoiceModels(
        IReadOnlyList<SpeechifyModelInfo> knownModels,
        IEnumerable<SpeechifyVoiceInfo> voices)
    {
        foreach (var voice in voices.Where(IsValidSpeechifyVoice))
        {
            foreach (var voiceModel in voice.Models.Where(IsValidSpeechifyVoiceModel))
            {
                var knownModel = knownModels.FirstOrDefault(m => string.Equals(m.Id, voiceModel.Name, StringComparison.OrdinalIgnoreCase));
                yield return new Model
                {
                    Id = $"{voiceModel.Name}/{voice.Id}".ToModelId(GetIdentifier()),
                    OwnedBy = ProviderName,
                    Type = "speech",
                    Name = BuildSpeechifyVoiceName(voiceModel.Name, voice),
                    Description = BuildSpeechifyVoiceDescription(voiceModel.Name, voice, knownModel),
                    Tags = BuildSpeechifyVoiceTags(voiceModel.Name, voice, voiceModel)
                };
            }
        }
    }

    private static IEnumerable<string> BuildSpeechifyModelTags(SpeechifyModelInfo model)
    {
        var tags = new List<string> { $"model:{model.Id}" };

        if (model.IsDefault is bool isDefault)
            tags.Add(isDefault ? "default:true" : "default:false");

        if (model.IsRecommended is bool isRecommended)
            tags.Add(isRecommended ? "recommended:true" : "recommended:false");

        foreach (var language in model.Languages.Take(100))
            tags.Add($"language:{language}");

        return tags;
    }

    private static string BuildSpeechifyVoiceName(string modelId, SpeechifyVoiceInfo voice)
    {
        var displayName = string.IsNullOrWhiteSpace(voice.DisplayName) ? voice.Id : voice.DisplayName;
        var locale = string.IsNullOrWhiteSpace(voice.Locale) ? "und" : voice.Locale;
        return $"{modelId} · {displayName} ({voice.Id}, {locale})";
    }

    private static string BuildSpeechifyVoiceDescription(string modelId, SpeechifyVoiceInfo voice, SpeechifyModelInfo? model)
    {
        var displayName = string.IsNullOrWhiteSpace(voice.DisplayName) ? voice.Id : voice.DisplayName;
        var parts = new List<string>
        {
            $"{ProviderName} voice {displayName} ({voice.Id}) on {modelId}."
        };

        if (!string.IsNullOrWhiteSpace(voice.Locale))
            parts.Add($"Locale: {voice.Locale}.");

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            parts.Add($"Gender: {voice.Gender}.");

        if (!string.IsNullOrWhiteSpace(voice.Type))
            parts.Add($"Type: {voice.Type}.");

        if (!string.IsNullOrWhiteSpace(model?.Description))
            parts.Add(model.Description!);

        return string.Join(" ", parts);
    }

    private static IEnumerable<string> BuildSpeechifyVoiceTags(string modelId, SpeechifyVoiceInfo voice, SpeechifyVoiceModelInfo voiceModel)
    {
        var tags = new List<string>
        {
            $"model:{modelId}",
            $"voice:{voice.Id}"
        };

        if (!string.IsNullOrWhiteSpace(voice.Locale))
            tags.Add($"locale:{voice.Locale}");

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"gender:{voice.Gender}");

        if (!string.IsNullOrWhiteSpace(voice.Type))
            tags.Add($"voice-type:{voice.Type}");

        if (!string.IsNullOrWhiteSpace(voice.PreviewAudio))
            tags.Add("preview-audio:true");

        if (!string.IsNullOrWhiteSpace(voice.AvatarImage))
            tags.Add("avatar-image:true");

        foreach (var language in voiceModel.Languages.Take(100))
            tags.Add($"language:{language}");

        foreach (var tag in voice.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Take(100))
            tags.Add(tag.Trim());

        return tags.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsValidSpeechifyVoice(SpeechifyVoiceInfo voice)
        => !string.IsNullOrWhiteSpace(voice.Id);

    private static bool IsValidSpeechifyVoiceModel(SpeechifyVoiceModelInfo model)
        => !string.IsNullOrWhiteSpace(model.Name);

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

    private static bool? ReadBool(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;

        if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var parsed))
            return parsed;

        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value.Trim());
            }
            else if (item.ValueKind == JsonValueKind.Number)
            {
                values.Add(item.GetRawText());
            }
        }

        return [.. values.Distinct(StringComparer.OrdinalIgnoreCase)];
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

    private sealed class SpeechifyModelInfo
    {
        public string Id { get; set; } = null!;
        public string? Name { get; set; }
        public bool? IsDefault { get; set; }
        public bool? IsRecommended { get; set; }
        public string? Description { get; set; }
        public IReadOnlyList<string> Languages { get; set; } = [];
    }

    private sealed class SpeechifyVoiceInfo
    {
        public string Id { get; set; } = null!;
        public string? DisplayName { get; set; }
        public string? Gender { get; set; }
        public string? Locale { get; set; }
        public string? Type { get; set; }
        public string? PreviewAudio { get; set; }
        public string? AvatarImage { get; set; }
        public IReadOnlyList<string> Tags { get; set; } = [];
        public IReadOnlyList<SpeechifyVoiceModelInfo> Models { get; set; } = [];
    }

    private sealed class SpeechifyVoiceModelInfo
    {
        public string Name { get; set; } = null!;
        public IReadOnlyList<string> Languages { get; set; } = [];
    }
}
