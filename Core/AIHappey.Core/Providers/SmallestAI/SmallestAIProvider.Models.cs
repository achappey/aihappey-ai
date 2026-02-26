using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SmallestAI;

public partial class SmallestAIProvider
{
    private const string TtsModelPrefix = "smallestai/";
    private const string LightningV31Model = "lightning-v3.1";
    private const string LightningV2Model = "lightning-v2";
    private const string PulseModel = "pulse";

    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        ApplyAuthHeader();

        var models = new List<Model>
        {
            BuildTranscriptionModel()
        };

        var v31Voices = await GetVoicesAsync(LightningV31Model, cancellationToken);
        var v2Voices = await GetVoicesAsync(LightningV2Model, cancellationToken);

        models.AddRange(BuildDynamicVoiceModels(LightningV31Model, v31Voices));
        models.AddRange(BuildDynamicVoiceModels(LightningV2Model, v2Voices));

        return models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private async Task<IReadOnlyList<SmallestAIVoice>> GetVoicesAsync(string model, CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync($"api/v1/{model}/get_voices", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} voices list failed for model '{model}' ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return ParseVoices(doc.RootElement);
    }

    private static IReadOnlyList<SmallestAIVoice> ParseVoices(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "voices", out var voicesEl)
            || voicesEl.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<SmallestAIVoice>();

        foreach (var item in voicesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var voiceId = ReadString(item, "voiceId") ?? ReadString(item, "voice_id") ?? ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            string? displayName = ReadString(item, "displayName") ?? ReadString(item, "name");
            string? gender = null;
            string? accent = null;
            List<string> languages = [];

            if (TryGetPropertyIgnoreCase(item, "tags", out var tagsEl)
                && tagsEl.ValueKind == JsonValueKind.Object)
            {
                gender = ReadString(tagsEl, "gender");
                accent = ReadString(tagsEl, "accent");

                if (TryGetPropertyIgnoreCase(tagsEl, "language", out var langEl))
                {
                    if (langEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var le in langEl.EnumerateArray())
                        {
                            if (le.ValueKind == JsonValueKind.String)
                            {
                                var l = le.GetString();
                                if (!string.IsNullOrWhiteSpace(l))
                                    languages.Add(l.Trim());
                            }
                        }
                    }
                    else if (langEl.ValueKind == JsonValueKind.String)
                    {
                        var l = langEl.GetString();
                        if (!string.IsNullOrWhiteSpace(l))
                            languages.Add(l.Trim());
                    }
                }
            }

            voices.Add(new SmallestAIVoice
            {
                VoiceId = voiceId.Trim(),
                DisplayName = displayName,
                Gender = gender,
                Accent = accent,
                Languages = languages
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            });
        }

        return voices;
    }

    private IEnumerable<Model> BuildDynamicVoiceModels(string model, IEnumerable<SmallestAIVoice> voices)
        => voices
            .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId))
            .Select(v => BuildVoiceModel(model, v));

    private Model BuildVoiceModel(string model, SmallestAIVoice voice)
    {
        var normalizedModelId = $"{TtsModelPrefix}{model}/{voice.VoiceId}";
        var languageText = voice.Languages.Count == 0
            ? "und"
            : string.Join(", ", voice.Languages);
        var genderText = string.IsNullOrWhiteSpace(voice.Gender) ? "unknown" : voice.Gender.Trim();
        var accentText = string.IsNullOrWhiteSpace(voice.Accent) ? "unspecified" : voice.Accent.Trim();
        var displayName = string.IsNullOrWhiteSpace(voice.DisplayName) ? voice.VoiceId : voice.DisplayName.Trim();

        return new Model
        {
            Id = normalizedModelId,
            OwnedBy = ProviderName,
            Type = "speech",
            Name = $"{model} / {displayName} ({genderText}, {languageText}, accent: {accentText})",
            Description = $"{ProviderName} {model} voice '{displayName}' (voiceId={voice.VoiceId}, gender={genderText}, language={languageText}, accent={accentText}).",
            Tags = BuildVoiceTags(model, voice)
        };
    }

    private static IEnumerable<string> BuildVoiceTags(string model, SmallestAIVoice voice)
    {
        var tags = new List<string>
        {
            $"model:{model}",
            $"voice:{voice.VoiceId}"
        };

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            tags.Add($"gender:{voice.Gender}");

        if (!string.IsNullOrWhiteSpace(voice.Accent))
            tags.Add($"accent:{voice.Accent}");

        foreach (var language in voice.Languages.Take(10))
            tags.Add($"language:{language}");

        return tags;
    }

    private Model BuildTranscriptionModel()
        => new()
        {
            Id = PulseModel.ToModelId(GetIdentifier()),
            OwnedBy = ProviderName,
            Type = "transcription",
            Name = "Pulse STT",
            Description = "SmallestAI Pulse pre-recorded speech-to-text model.",
            Tags = ["model:pulse", "stt"]
        };

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

    private sealed class SmallestAIVoice
    {
        public string VoiceId { get; set; } = null!;
        public string? DisplayName { get; set; }
        public string? Gender { get; set; }
        public string? Accent { get; set; }
        public List<string> Languages { get; set; } = [];
    }
}

