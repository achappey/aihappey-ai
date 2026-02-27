using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Astica;

public partial class AsticaProvider
{
    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        var apiKey = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(apiKey))
            return [];

        ApplyAuthHeader(apiKey);

        var models = new List<Model>();
        models.AddRange(GetIdentifier().GetModels());

        var voices = await GetVoicesAsync(apiKey, cancellationToken);

        models.AddRange(voices
            .Where(IsValidVoice)
            .GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(BuildVoiceModel));

        return [.. models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private async Task<IReadOnlyList<AsticaVoice>> GetVoicesAsync(string apiKey, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tkn"] = apiKey
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "api/voice_list")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var respBody = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} voice list failed ({(int)resp.StatusCode}): {respBody}");

        using var doc = JsonDocument.Parse(respBody);
        return ParseVoices(doc.RootElement);
    }

    private static IReadOnlyList<AsticaVoice> ParseVoices(JsonElement root)
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

        var voices = new List<AsticaVoice>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var voiceId = ReadString(item, "voice")
                ?? ReadString(item, "voice_id")
                ?? ReadString(item, "voiceId")
                ?? ReadString(item, "id");

            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            var useCases = ReadStringArray(item, "use_cases")
                ?? ReadStringArray(item, "useCases")
                ?? ReadStringArray(item, "usecases")
                ?? [];

            voices.Add(new AsticaVoice
            {
                Id = voiceId.Trim(),
                Name = ReadString(item, "name")?.Trim(),
                Description = ReadString(item, "desc")?.Trim() ?? ReadString(item, "description")?.Trim(),
                Gender = ReadString(item, "gender")?.Trim(),
                Age = ReadString(item, "age")?.Trim(),
                UseCases = [.. useCases
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())]
            });
        }

        return voices;
    }

    private Model BuildVoiceModel(AsticaVoice voice)
        => new()
        {
            Id = $"{ProviderId}/{voice.Id}",
            OwnedBy = ProviderName,
            Type = "speech",
            Name = BuildVoiceName(voice),
            Description = BuildVoiceDescription(voice),
            Tags = BuildVoiceTags(voice)
        };

    private static string BuildVoiceName(AsticaVoice voice)
    {
        var humanName = string.IsNullOrWhiteSpace(voice.Name) ? voice.Id : voice.Name;
        var gender = string.IsNullOrWhiteSpace(voice.Gender) ? null : voice.Gender;
        var age = string.IsNullOrWhiteSpace(voice.Age) ? null : voice.Age;
        var useCases = voice.UseCases.Count == 0 ? null : string.Join(", ", voice.UseCases.Take(2));

        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(gender))
            parts.Add(gender!);
        if (!string.IsNullOrWhiteSpace(age))
            parts.Add(age!);
        if (!string.IsNullOrWhiteSpace(useCases))
            parts.Add(useCases!);

        if (parts.Count == 0)
            return humanName!;

        return $"{humanName} ({string.Join("; ", parts)})";
    }

    private static string BuildVoiceDescription(AsticaVoice voice)
    {
        var desc = string.IsNullOrWhiteSpace(voice.Description)
            ? $"{ProviderName} TTS voice {voice.Id}."
            : $"{ProviderName} TTS voice {voice.Id}. {voice.Description.Trim()}";

        if (voice.UseCases.Count == 0)
            return desc;

        return $"{desc} Use cases: {string.Join(", ", voice.UseCases)}.";
    }

    private static IEnumerable<string> BuildVoiceTags(AsticaVoice voice)
    {
        var tags = new List<string>
        {
            $"voice:{voice.Id}"
        };

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"gender:{voice.Gender.Trim().ToLowerInvariant()}");

        if (!string.IsNullOrWhiteSpace(voice.Age))
            tags.Add($"age:{voice.Age.Trim().ToLowerInvariant()}");

        foreach (var useCase in voice.UseCases.Take(10))
            tags.Add($"use_case:{useCase.Trim().ToLowerInvariant().Replace(' ', '-')}");

        return tags;
    }

    private static bool IsValidVoice(AsticaVoice voice)
        => !string.IsNullOrWhiteSpace(voice.Id);

    private static string? ReadString(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();

        if (el.ValueKind == JsonValueKind.Number)
            return el.GetRawText();

        if (el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return el.GetBoolean().ToString();

        return null;
    }

    private static List<string>? ReadStringArray(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.String)
        {
            var value = el.GetString();
            return string.IsNullOrWhiteSpace(value) ? [] : [value];
        }

        if (el.ValueKind != JsonValueKind.Array)
            return null;

        var values = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    values.Add(s);
            }
        }

        return values;
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

    private sealed class AsticaVoice
    {
        public string Id { get; set; } = null!;

        public string? Name { get; set; }

        public string? Description { get; set; }

        public string? Gender { get; set; }

        public string? Age { get; set; }

        public List<string> UseCases { get; set; } = [];
    }
}

