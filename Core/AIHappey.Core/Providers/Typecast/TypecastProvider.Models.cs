using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Typecast;

public partial class TypecastProvider
{
    private const string TypecastTtsModelPrefix = "typecast/";

    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        ApplyAuthHeader();

        var voices = await GetVoicesAsync(cancellationToken);
        return BuildDynamicVoiceModels(voices)
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private async Task<IReadOnlyList<TypecastVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync("v2/voices", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return ParseVoices(doc.RootElement);
    }

    private static IReadOnlyList<TypecastVoice> ParseVoices(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<TypecastVoice>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var voiceId = ReadString(item, "voice_id") ?? ReadString(item, "voiceId") ?? ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            var models = ParseModels(item);
            if (models.Count == 0)
                continue;

            voices.Add(new TypecastVoice
            {
                VoiceId = voiceId.Trim(),
                VoiceName = ReadString(item, "voice_name") ?? ReadString(item, "voiceName") ?? voiceId.Trim(),
                Gender = ReadString(item, "gender"),
                Age = ReadString(item, "age"),
                UseCases = ParseStringArray(item, "use_cases", "useCases"),
                Models = models
            });
        }

        return voices;
    }

    private static List<TypecastModelInfo> ParseModels(JsonElement voice)
    {
        if (!TryGetPropertyIgnoreCase(voice, "models", out var modelsEl)
            || modelsEl.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<TypecastModelInfo>();
        foreach (var modelEl in modelsEl.EnumerateArray())
        {
            if (modelEl.ValueKind != JsonValueKind.Object)
                continue;

            var version = ReadString(modelEl, "version")?.Trim();
            if (string.IsNullOrWhiteSpace(version))
                continue;

            var emotions = ParseStringArray(modelEl, "emotions");
            result.Add(new TypecastModelInfo
            {
                Version = version,
                Emotions = emotions
            });
        }

        return result;
    }

    private IEnumerable<Model> BuildDynamicVoiceModels(IEnumerable<TypecastVoice> voices)
    {
        foreach (var voice in voices)
        {
            foreach (var model in voice.Models.Where(m => !string.IsNullOrWhiteSpace(m.Version)))
            {
                yield return BuildVoiceModel(voice, model);
            }
        }
    }

    private Model BuildVoiceModel(TypecastVoice voice, TypecastModelInfo model)
    {
        var normalizedId = $"{TypecastTtsModelPrefix}{model.Version}/{voice.VoiceId}";
        var gender = string.IsNullOrWhiteSpace(voice.Gender) ? "unknown gender" : voice.Gender.Trim();
        var age = string.IsNullOrWhiteSpace(voice.Age) ? "unknown age" : voice.Age.Trim().Replace('_', ' ');
        var useCases = voice.UseCases.Count == 0 ? "general" : string.Join(", ", voice.UseCases.Take(4));
        var displayName = string.IsNullOrWhiteSpace(voice.VoiceName) ? voice.VoiceId : voice.VoiceName.Trim();

        return new Model
        {
            Id = normalizedId,
            OwnedBy = ProviderName,
            Type = "speech",
            Name = $"{model.Version} / {displayName} ({age}, {gender}, use cases: {useCases})",
            Description = $"{ProviderName} voice '{displayName}' (voice_id={voice.VoiceId}) on model '{model.Version}' with age={age}, gender={gender}, use_cases={useCases}.",
            Tags = BuildVoiceTags(voice, model)
        };
    }

    private static IEnumerable<string> BuildVoiceTags(TypecastVoice voice, TypecastModelInfo model)
    {
        var tags = new List<string>
        {
            $"model:{model.Version}",
            $"voice:{voice.VoiceId}"
        };

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"gender:{voice.Gender}");

        if (!string.IsNullOrWhiteSpace(voice.Age))
            tags.Add($"age:{voice.Age}");

        foreach (var uc in voice.UseCases.Take(10))
            tags.Add($"usecase:{uc}");

        foreach (var emotion in model.Emotions.Take(12))
            tags.Add($"emotion:{emotion}");

        return tags;
    }

    private static List<string> ParseStringArray(JsonElement obj, params string[] names)
    {
        JsonElement arr = default;
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(obj, name, out arr))
                break;
        }

        if (arr.ValueKind == JsonValueKind.Undefined)
            return [];

        var values = new List<string>();
        if (arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        values.Add(s.Trim());
                }
            }
        }
        else if (arr.ValueKind == JsonValueKind.String)
        {
            var s = arr.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                values.Add(s.Trim());
        }

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private sealed class TypecastVoice
    {
        public string VoiceId { get; set; } = null!;
        public string? VoiceName { get; set; }
        public string? Gender { get; set; }
        public string? Age { get; set; }
        public List<string> UseCases { get; set; } = [];
        public List<TypecastModelInfo> Models { get; set; } = [];
    }

    private sealed class TypecastModelInfo
    {
        public string Version { get; set; } = null!;
        public List<string> Emotions { get; set; } = [];
    }
}

