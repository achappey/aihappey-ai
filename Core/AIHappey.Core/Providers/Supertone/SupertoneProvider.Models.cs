using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Supertone;

public partial class SupertoneProvider
{
    private const string SupertoneModelPrefix = "supertone/";

    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        ApplyAuthHeader();

        var voices = await GetAllVoicesAsync(cancellationToken);

        return [.. BuildDynamicVoiceModels(voices)
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private async Task<IReadOnlyList<SupertoneVoice>> GetAllVoicesAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 100;

        var voices = new List<SupertoneVoice>();
        string? nextPageToken = null;
        var safety = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = $"v1/voices?page_size={pageSize}";
            if (!string.IsNullOrWhiteSpace(nextPageToken))
                path += $"&next_page_token={Uri.EscapeDataString(nextPageToken)}";

            using var resp = await _client.GetAsync(path, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var pageVoices = ParseVoicePage(root);
            voices.AddRange(pageVoices);

            nextPageToken = ReadCaseInsensitiveString(root, "next_page_token");
            if (string.IsNullOrWhiteSpace(nextPageToken))
                break;

            safety++;
            if (safety > 300)
                break;
        }

        return [.. voices
            .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId))
            .GroupBy(v => v.VoiceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private static IReadOnlyList<SupertoneVoice> ParseVoicePage(JsonElement root)
    {
        if (!TryGetCaseInsensitiveProperty(root, "items", out var items)
            || items.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<SupertoneVoice>();

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var voiceId = ReadCaseInsensitiveString(item, "voice_id");
            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            var models = ParseCaseInsensitiveStringArray(item, "models");
            if (models.Count == 0)
                continue;

            var useCases = ParseCaseInsensitiveStringArray(item, "use_cases");
            if (useCases.Count == 0)
            {
                var fallbackUseCase = ReadCaseInsensitiveString(item, "use_case");
                if (!string.IsNullOrWhiteSpace(fallbackUseCase))
                    useCases.Add(fallbackUseCase.Trim());
            }

            voices.Add(new SupertoneVoice
            {
                VoiceId = voiceId.Trim(),
                Name = ReadCaseInsensitiveString(item, "name"),
                Description = ReadCaseInsensitiveString(item, "description"),
                Age = ReadCaseInsensitiveString(item, "age"),
                Gender = ReadCaseInsensitiveString(item, "gender"),
                UseCases = useCases,
                Languages = ParseCaseInsensitiveStringArray(item, "language"),
                Styles = ParseCaseInsensitiveStringArray(item, "styles"),
                Models = models
            });
        }

        return voices;
    }

    private IEnumerable<Model> BuildDynamicVoiceModels(IEnumerable<SupertoneVoice> voices)
    {
        foreach (var voice in voices)
        {
            foreach (var modelId in voice.Models.Where(m => !string.IsNullOrWhiteSpace(m)))
            {
                yield return BuildVoiceModel(voice, modelId.Trim());
            }
        }
    }

    private Model BuildVoiceModel(SupertoneVoice voice, string modelId)
    {
        var id = $"{SupertoneModelPrefix}{modelId}/{voice.VoiceId}";

        var displayName = string.IsNullOrWhiteSpace(voice.Name) ? voice.VoiceId : voice.Name.Trim();
        var age = NormalizeMetaValue(voice.Age, "unknown age");
        var gender = NormalizeMetaValue(voice.Gender, "unknown gender");
        var useCases = voice.UseCases.Count == 0
            ? "general"
            : string.Join(", ", voice.UseCases.Take(4).Select(u => u.Trim()));

        var descriptionDetail = string.IsNullOrWhiteSpace(voice.Description)
            ? ""
            : $" {voice.Description.Trim()}";

        return new Model
        {
            Id = id,
            OwnedBy = ProviderName,
            Type = "speech",
            Name = $"{modelId} / {displayName} ({age}, {gender}, use cases: {useCases})",
            Description = $"{ProviderName} voice '{displayName}' (voice_id={voice.VoiceId}) on model '{modelId}' with age={age}, gender={gender}, use_cases={useCases}.{descriptionDetail}",
            Tags = BuildVoiceTags(voice, modelId)
        };
    }

    private static IEnumerable<string> BuildVoiceTags(SupertoneVoice voice, string modelId)
    {
        var tags = new List<string>
        {
            $"model:{modelId}",
            $"voice:{voice.VoiceId}"
        };

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"gender:{voice.Gender.Trim()}");

        if (!string.IsNullOrWhiteSpace(voice.Age))
            tags.Add($"age:{voice.Age.Trim()}");

        foreach (var uc in voice.UseCases.Take(10).Where(uc => !string.IsNullOrWhiteSpace(uc)))
            tags.Add($"usecase:{uc.Trim()}");

        foreach (var lang in voice.Languages.Take(12).Where(l => !string.IsNullOrWhiteSpace(l)))
            tags.Add($"lang:{lang.Trim()}");

        foreach (var style in voice.Styles.Take(12).Where(s => !string.IsNullOrWhiteSpace(s)))
            tags.Add($"style:{style.Trim()}");

        return tags;
    }

    private static string NormalizeMetaValue(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim().Replace('_', ' ').Replace('-', ' ');

    private static List<string> ParseCaseInsensitiveStringArray(JsonElement obj, string propertyName)
    {
        if (!TryGetCaseInsensitiveProperty(obj, propertyName, out var arr))
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

        return [.. values.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string? ReadCaseInsensitiveString(JsonElement obj, string propertyName)
    {
        if (!TryGetCaseInsensitiveProperty(obj, propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();

        if (el.ValueKind == JsonValueKind.Number)
            return el.GetRawText();

        return null;
    }

    private static bool TryGetCaseInsensitiveProperty(JsonElement obj, string propertyName, out JsonElement value)
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

    private sealed class SupertoneVoice
    {
        public string VoiceId { get; set; } = null!;
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Age { get; set; }
        public string? Gender { get; set; }
        public List<string> UseCases { get; set; } = [];
        public List<string> Languages { get; set; } = [];
        public List<string> Styles { get; set; } = [];
        public List<string> Models { get; set; } = [];
    }
}

