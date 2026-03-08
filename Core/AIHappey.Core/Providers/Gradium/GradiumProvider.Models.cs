using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Gradium;

public partial class GradiumProvider
{
    private const string BaseSpeechModel = "default";

    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var models = new List<Model>
        {
            new()
            {
                Id = BaseSpeechModel.ToModelId(GetIdentifier()),
                OwnedBy = ProviderName,
                Type = "speech",
                Name = BaseSpeechModel,
                Description = $"{ProviderName} base TTS model. Voice may be supplied via request.voice, providerOptions.gradium.voice_id, or model id.",
                Tags = ["tts", $"model:{BaseSpeechModel}", "base"]
            }
        };

        var voices = await GetVoicesAsync(cancellationToken);
        models.AddRange(BuildDynamicVoiceModels(voices));

        return [.. models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private async Task<IReadOnlyList<GradiumVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        const int limit = 100;
        var skip = 0;
        var voices = new List<GradiumVoice>();

        while (true)
        {
            using var resp = await _client.GetAsync($"api/voices/?skip={skip}&limit={limit}&include_catalog=true", cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var page = ParseVoices(doc.RootElement);
            if (page.Count == 0)
                break;

            voices.AddRange(page);

            if (page.Count < limit)
                break;

            skip += limit;
        }

        return [.. voices
            .Where(IsValidVoice)
            .GroupBy(v => v.VoiceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private IEnumerable<Model> BuildDynamicVoiceModels(IEnumerable<GradiumVoice> voices)
        => voices
            .Where(IsValidVoice)
            .Select(v => new Model
            {
                Id = $"{BaseSpeechModel}/{v.VoiceId}".ToModelId(GetIdentifier()),
                OwnedBy = ProviderName,
                Type = "speech",
                Name = $"{BuildVoiceDisplayName(v)}",
                Description = BuildVoiceDescription(v),
                Tags = BuildVoiceTags(v)
            });

    private static IReadOnlyList<GradiumVoice> ParseVoices(JsonElement root)
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
            {
                array = root;
            }
        }
        else
        {
            return [];
        }

        var voices = new List<GradiumVoice>();

        if (array.ValueKind == JsonValueKind.Object)
        {
            ParseVoiceObject(array, voices);
            return [.. voices
                .Where(IsValidVoice)
                .GroupBy(v => v.VoiceId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())];
        }

        if (array.ValueKind != JsonValueKind.Array)
            return [];

        foreach (var item in array.EnumerateArray())
            ParseVoiceObject(item, voices);

        return [.. voices
            .Where(IsValidVoice)
            .GroupBy(v => v.VoiceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private static void ParseVoiceObject(JsonElement item, List<GradiumVoice> voices)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;

        var voiceId = ReadString(item, "uid")
            ?? ReadString(item, "voice_id")
            ?? ReadString(item, "voiceId")
            ?? ReadString(item, "id");

        if (string.IsNullOrWhiteSpace(voiceId))
            return;

        voices.Add(new GradiumVoice
        {
            VoiceId = voiceId.Trim(),
            Name = ReadString(item, "name"),
            Description = ReadString(item, "description"),
            Language = ReadString(item, "language"),
            IsCatalog = ReadBool(item, "is_catalog"),
            IsProClone = ReadBool(item, "is_pro_clone"),
            Tags = ReadStringArray(item, "tags")
        });
    }

    private static string BuildVoiceDisplayName(GradiumVoice voice)
    {
        var baseName = string.IsNullOrWhiteSpace(voice.Name) ? voice.VoiceId : voice.Name.Trim();
        var language = string.IsNullOrWhiteSpace(voice.Language) ? "unknown" : voice.Language.Trim();
        return $"{baseName} ({language})";
    }

    private static string BuildVoiceDescription(GradiumVoice voice)
    {
        return voice.Description ?? string.Empty;
    }

    private static IEnumerable<string> BuildVoiceTags(GradiumVoice voice)
    {
        var tags = new List<string>
        {
            "tts",
            $"model:{BaseSpeechModel}",
            $"voice:{voice.VoiceId}"
        };

        if (!string.IsNullOrWhiteSpace(voice.Language))
            tags.Add($"language:{voice.Language.Trim()}");

        if (voice.IsCatalog is not null)
            tags.Add($"catalog:{voice.IsCatalog.Value.ToString().ToLowerInvariant()}");

        if (voice.IsProClone is not null)
            tags.Add($"pro_clone:{voice.IsProClone.Value.ToString().ToLowerInvariant()}");

        foreach (var tag in voice.Tags.Take(10))
        {
            if (!string.IsNullOrWhiteSpace(tag))
                tags.Add($"tag:{tag.Trim().ToLowerInvariant().Replace(' ', '-')}");
        }

        return tags;
    }

    private static bool IsValidVoice(GradiumVoice voice)
        => !string.IsNullOrWhiteSpace(voice.VoiceId);

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

        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(el.GetString(), out var b) => b,
            _ => null
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];

        return [.. el.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())];
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

    private sealed class GradiumVoice
    {
        public string VoiceId { get; set; } = null!;
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Language { get; set; }
        public bool? IsCatalog { get; set; }
        public bool? IsProClone { get; set; }
        public IReadOnlyList<string> Tags { get; set; } = [];
    }
}
