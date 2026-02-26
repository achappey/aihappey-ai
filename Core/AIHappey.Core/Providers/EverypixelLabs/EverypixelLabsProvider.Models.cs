using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.EverypixelLabs;

public partial class EverypixelLabsProvider
{
    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        ApplyAuthHeader();

        var voices = await GetVoicesAsync(cancellationToken);
        return BuildDynamicVoiceModels(voices);
    }

    private async Task<IReadOnlyList<EverypixelVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync("v1/tts/voices", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return ParseVoices(doc.RootElement);
    }

    private static IReadOnlyList<EverypixelVoice> ParseVoices(JsonElement root)
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
                // Some providers return a single object for a single voice.
                array = root;
            }
        }
        else
        {
            return [];
        }

        var voices = new List<EverypixelVoice>();

        if (array.ValueKind == JsonValueKind.Object)
        {
            ParseVoiceObject(array, voices);
            return [.. voices
                .Where(IsValidVoice)
                .GroupBy(v => v.ApiName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())];
        }

        if (array.ValueKind != JsonValueKind.Array)
            return [];

        foreach (var item in array.EnumerateArray())
            ParseVoiceObject(item, voices);

        return [.. voices
            .Where(IsValidVoice)
            .GroupBy(v => v.ApiName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private static void ParseVoiceObject(JsonElement item, List<EverypixelVoice> voices)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return;

        var apiName = ReadString(item, "api_name")
            ?? ReadString(item, "apiName")
            ?? ReadString(item, "voice")
            ?? ReadString(item, "id");

        if (string.IsNullOrWhiteSpace(apiName))
            return;

        voices.Add(new EverypixelVoice
        {
            ApiName = apiName.Trim(),
            Name = ReadString(item, "name"),
            Age = ReadString(item, "age"),
            Gender = ReadString(item, "gender"),
            Language = ReadString(item, "language")
        });
    }

    private IEnumerable<Model> BuildDynamicVoiceModels(IEnumerable<EverypixelVoice> voices)
        => voices
            .Where(IsValidVoice)
            .Select(v => new Model
            {
                Id = v.ApiName.ToModelId(GetIdentifier()),
                OwnedBy = ProviderName,
                Type = "speech",
                Name = BuildVoiceDisplayName(v),
                Description = BuildVoiceDescription(v),
                Tags = BuildVoiceTags(v)
            });

    private static IEnumerable<string> BuildVoiceTags(EverypixelVoice voice)
    {
        var tags = new List<string>();

        if (!string.IsNullOrWhiteSpace(voice.Language))
            tags.Add($"language:{voice.Language}");

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"gender:{voice.Gender}");

        if (!string.IsNullOrWhiteSpace(voice.Age))
            tags.Add($"age:{voice.Age}");

        return tags;
    }

    private static string BuildVoiceDisplayName(EverypixelVoice voice)
    {
        var baseName = string.IsNullOrWhiteSpace(voice.Name)
            ? voice.ApiName
            : voice.Name.Trim();

        var language = string.IsNullOrWhiteSpace(voice.Language) ? "unknown" : voice.Language.Trim();
        var gender = string.IsNullOrWhiteSpace(voice.Gender) ? "unknown" : voice.Gender.Trim();
        var age = string.IsNullOrWhiteSpace(voice.Age) ? "unknown" : voice.Age.Trim();

        return $"{baseName} ({language}, {gender}, {age})";
    }

    private static string BuildVoiceDescription(EverypixelVoice voice)
    {
        var language = string.IsNullOrWhiteSpace(voice.Language) ? "unknown language" : voice.Language.Trim();
        var gender = string.IsNullOrWhiteSpace(voice.Gender) ? "unknown gender" : voice.Gender.Trim();
        var age = string.IsNullOrWhiteSpace(voice.Age) ? "unknown age" : voice.Age.Trim();

        return $"{ProviderName} TTS voice {voice.ApiName} ({language}, {gender}, {age}).";
    }

    private static bool IsValidVoice(EverypixelVoice voice)
        => !string.IsNullOrWhiteSpace(voice.ApiName);

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

    private sealed class EverypixelVoice
    {
        public string ApiName { get; set; } = null!;

        public string? Name { get; set; }

        public string? Age { get; set; }

        public string? Gender { get; set; }

        public string? Language { get; set; }
    }
}

