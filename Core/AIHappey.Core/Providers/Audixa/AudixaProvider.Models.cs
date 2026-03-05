using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Audixa;

public partial class AudixaProvider
{
    private const string ProviderName = nameof(Audixa);

    private static readonly string[] StaticTtsModels = ["base", "advanced"];

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken)
    {
        var models = new List<Model>();
        models.AddRange(BuildStaticModels());

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return models;

        ApplyAuthHeader();

        var voices = await GetVoicesAsync(cancellationToken);
        models.AddRange(BuildDynamicVoiceModels(voices));

        return [.. models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private IEnumerable<Model> BuildStaticModels()
        => StaticTtsModels.Select(modelId => new Model
        {
            Id = modelId.ToModelId(GetIdentifier()),
            OwnedBy = ProviderName,
            Type = "speech",
            Name = $"Audixa {ToTitle(modelId)}",
            Description = $"Audixa {ToTitle(modelId)}"
        });

    private IEnumerable<Model> BuildDynamicVoiceModels(IEnumerable<AudixaVoice> voices)
        => voices
            .Where(IsValidVoice)
            .Select(v => new Model
            {
                Id = $"{v.Model}/{v.VoiceId}".ToModelId(GetIdentifier()),
                OwnedBy = ProviderName,
                Type = "speech",
                Name = BuildVoiceDisplayName(v),
                Description = BuildVoiceDescription(v),
                Tags = BuildVoiceTags(v)
            });

    private async Task<IReadOnlyList<AudixaVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        const int limit = 100;
        var offset = 0;
        var voices = new List<AudixaVoice>();

        while (true)
        {
            var path = $"v3/voices?limit={limit}&offset={offset}";
            using var resp = await _client.GetAsync(path, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var pageVoices = ParseVoices(doc.RootElement);

            if (pageVoices.Count == 0)
                break;

            voices.AddRange(pageVoices);

            var pageLength = ReadInt(doc.RootElement, "length") ?? pageVoices.Count;
            if (pageLength <= 0 || pageLength < limit)
                break;

            offset += pageLength;
        }

        return [.. voices
            .Where(IsValidVoice)
            .GroupBy(v => $"{v.Model}:{v.VoiceId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private static IReadOnlyList<AudixaVoice> ParseVoices(JsonElement root)
    {
        JsonElement array = default;

        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (!TryGetPropertyIgnoreCase(root, "voices", out array)
                && !TryGetPropertyIgnoreCase(root, "data", out array)
                && !TryGetPropertyIgnoreCase(root, "items", out array)
                && !TryGetPropertyIgnoreCase(root, "results", out array))
                return [];
        }
        else
        {
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<AudixaVoice>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var voiceId = ReadString(item, "voice_id")
                ?? ReadString(item, "voiceId")
                ?? ReadString(item, "id");

            var model = NormalizeModel(ReadString(item, "model"));

            if (string.IsNullOrWhiteSpace(voiceId) || string.IsNullOrWhiteSpace(model))
                continue;

            voices.Add(new AudixaVoice
            {
                VoiceId = voiceId.Trim(),
                Model = model,
                Name = ReadString(item, "name"),
                Gender = ReadString(item, "gender"),
                Accent = ReadString(item, "accent"),
                Description = ReadString(item, "description"),
                IsCustom = ReadBool(item, "is_custom") ?? ReadBool(item, "isCustom"),
                IsFree = ReadBool(item, "free")
            });
        }

        return voices;
    }

    private static IEnumerable<string> BuildVoiceTags(AudixaVoice voice)
    {
        var tags = new List<string>
        {
            $"model:{voice.Model}",
            $"voice:{voice.VoiceId}"
        };

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"gender:{voice.Gender}");

        if (!string.IsNullOrWhiteSpace(voice.Accent))
            tags.Add($"accent:{voice.Accent}");

        if (voice.IsCustom is bool isCustom)
            tags.Add(isCustom ? "custom:true" : "custom:false");

        if (voice.IsFree is bool isFree)
            tags.Add(isFree ? "free:true" : "free:false");

        return tags;
    }

    private static string BuildVoiceDisplayName(AudixaVoice voice)
    {
        var name = string.IsNullOrWhiteSpace(voice.Name) ? voice.VoiceId : voice.Name.Trim();
        var model = ToTitle(voice.Model);
        return $"{model} · {name} ({voice.VoiceId})";
    }

    private static string BuildVoiceDescription(AudixaVoice voice)
    {
        if (!string.IsNullOrWhiteSpace(voice.Description))
            return $"{ProviderName} {ToTitle(voice.Model)} voice {voice.VoiceId}: {voice.Description.Trim()}";

        var accent = string.IsNullOrWhiteSpace(voice.Accent) ? "unknown accent" : voice.Accent.Trim();
        var gender = string.IsNullOrWhiteSpace(voice.Gender) ? "unknown gender" : voice.Gender.Trim();
        return $"{ProviderName} {ToTitle(voice.Model)} voice {voice.VoiceId} ({accent}, {gender}).";
    }

    private static bool IsValidVoice(AudixaVoice voice)
        => !string.IsNullOrWhiteSpace(voice.VoiceId)
            && (string.Equals(voice.Model, "base", StringComparison.OrdinalIgnoreCase)
                || string.Equals(voice.Model, "advanced", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var normalized = model.Trim().ToLowerInvariant();
        return normalized switch
        {
            "base" => "base",
            "advanced" => "advanced",
            _ => string.Empty
        };
    }

    private static string ToTitle(string text)
        => string.IsNullOrWhiteSpace(text)
            ? text
            : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();

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

    private static int? ReadInt(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
            return n;

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out n))
            return n;

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

    private sealed class AudixaVoice
    {
        public string VoiceId { get; set; } = null!;
        public string Model { get; set; } = null!;
        public string? Name { get; set; }
        public string? Gender { get; set; }
        public string? Accent { get; set; }
        public string? Description { get; set; }
        public bool? IsCustom { get; set; }
        public bool? IsFree { get; set; }
    }
}

