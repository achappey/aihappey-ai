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

        var baseModels = GetIdentifier().GetModels();

        var voices = await GetVoicesAsync(cancellationToken);
        var voiceModels = BuildDynamicVoiceModels(baseModels, voices);

        return baseModels.Concat(voiceModels);
    }

    private IEnumerable<Model> BuildDynamicVoiceModels(
    IEnumerable<Model> baseModels,
    IEnumerable<VerbatikVoice> voices)
    {
        var validVoices = voices
            .Where(IsValidVoice)
            .ToArray();

        return baseModels.SelectMany(baseModel =>
            validVoices.Select(voice => CreateVoiceModel(baseModel, voice)));
    }

    private string GetLocalModelId(string modelId)
    {
        var providerPrefix = $"{GetIdentifier()}/";

        return modelId.StartsWith(
                providerPrefix,
                StringComparison.OrdinalIgnoreCase)
            ? modelId[providerPrefix.Length..]
            : modelId;
    }

    private static IEnumerable<string> BuildVoiceTags(
        Model baseModel,
        VerbatikVoice voice)
    {
        var tags = new HashSet<string>(
            baseModel.Tags ?? [],
            StringComparer.OrdinalIgnoreCase);

        tags.Add($"voice:{voice.Id}");

        if (!string.IsNullOrWhiteSpace(voice.LanguageCode))
            tags.Add($"language:{voice.LanguageCode.Trim()}");

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"gender:{voice.Gender.Trim()}");

        return tags;
    }

    private static bool IsValidBaseModel(Model model)
        => !string.IsNullOrWhiteSpace(model.Id)
           && string.Equals(model.Type, "speech", StringComparison.OrdinalIgnoreCase);

    private Model CreateVoiceModel(
        Model baseModel,
        VerbatikVoice voice)
    {
        var baseModelId = GetLocalModelId(baseModel.Id);

        return new Model
        {
            Id = $"{baseModelId}/{voice.Id}".ToModelId(GetIdentifier()),
            OwnedBy = baseModel.OwnedBy ?? ProviderName,
            Type = baseModel.Type ?? "speech",
            Name = $"{baseModel.Name ?? baseModelId} · {BuildVoiceDisplayName(voice)}",
            Description = $"{baseModel.Description ?? $"{ProviderName} TTS model {baseModelId}"} Voice: {voice.Id}.",
            Pricing = baseModel.Pricing,
            Tags = BuildVoiceTags(baseModel, voice)
        };
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

