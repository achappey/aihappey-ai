using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.VoiceAI;

public partial class VoiceAIProvider
{
    private static readonly string[] BaseSpeechModels =
    [
        "voiceai-tts-v1-latest",
        "voiceai-tts-v1-2026-02-10",
        "voiceai-tts-multilingual-v1-latest",
        "voiceai-tts-multilingual-v1-2026-02-10"
    ];

    private static readonly string[] SupportedLanguages = ["en", "ca", "sv", "es", "fr", "de", "it", "pt", "pl", "ru", "nl"];

    private async Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var models = new List<Model>();

        models.AddRange(BaseSpeechModels.Select(model => new Model
        {
            Id = model.ToModelId(GetIdentifier()),
            OwnedBy = ProviderName,
            Type = "speech",
            Name = model,
            Description = $"{ProviderName} base TTS model. Voice and language may be supplied via request, provider options, or model id.",
            Tags = ["tts", $"model:{model}", "base"]
        }));

        var voices = await GetVoicesAsync(cancellationToken);
        models.AddRange(BuildDynamicVoiceLanguageModels(voices));

        return [.. models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private async Task<IReadOnlyList<VoiceAIVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync("api/v1/tts/voices", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return ParseVoices(doc.RootElement);
    }

    private IEnumerable<Model> BuildDynamicVoiceLanguageModels(IEnumerable<VoiceAIVoice> voices)
        => voices
            .Where(IsValidVoice)
            .SelectMany(voice => BaseSpeechModels.SelectMany(model => SupportedLanguages.Select(language => new Model
            {
                Id = BuildCompositeModelId(model, voice.VoiceId, language).ToModelId(GetIdentifier()),
                OwnedBy = ProviderName,
                Type = "speech",
                Name = $"{model} · {BuildVoiceDisplayName(voice)} · {language.ToUpperInvariant()}",
                Description = BuildVoiceDescription(model, voice, language),
                Tags = BuildVoiceTags(model, voice, language)
            })));

    private static IReadOnlyList<VoiceAIVoice> ParseVoices(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<VoiceAIVoice>();

        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var voiceId = ReadString(item, "voice_id")
                ?? ReadString(item, "voiceId")
                ?? ReadString(item, "id");

            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            voices.Add(new VoiceAIVoice
            {
                VoiceId = voiceId.Trim(),
                Name = ReadString(item, "name")?.Trim(),
                Status = ReadString(item, "status")?.Trim(),
                Visibility = ReadString(item, "voice_visibility")?.Trim()
            });
        }

        return [.. voices
            .Where(IsValidVoice)
            .GroupBy(v => v.VoiceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private static string BuildCompositeModelId(string baseModel, string voiceId, string language)
        => $"{baseModel}/{voiceId}/{language}";

    private static string BuildVoiceDisplayName(VoiceAIVoice voice)
        => string.IsNullOrWhiteSpace(voice.Name) ? voice.VoiceId : $"{voice.Name.Trim()} ({voice.VoiceId})";

    private static string BuildVoiceDescription(string baseModel, VoiceAIVoice voice, string language)
        => $"{ProviderName} TTS model '{baseModel}' with voice '{voice.VoiceId}' and language '{language}'.";

    private static IEnumerable<string> BuildVoiceTags(string baseModel, VoiceAIVoice voice, string language)
    {
        var tags = new List<string>
        {
            "tts",
            $"model:{baseModel}",
            $"voice:{voice.VoiceId}",
            $"language:{language}"
        };

        if (!string.IsNullOrWhiteSpace(voice.Status))
            tags.Add($"status:{voice.Status.Trim().ToLowerInvariant()}");

        if (!string.IsNullOrWhiteSpace(voice.Visibility))
            tags.Add($"visibility:{voice.Visibility.Trim().ToLowerInvariant()}");

        return tags;
    }

    private static bool IsValidVoice(VoiceAIVoice voice)
        => !string.IsNullOrWhiteSpace(voice.VoiceId)
           && string.Equals(voice.Status, "AVAILABLE", StringComparison.OrdinalIgnoreCase);

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

    private sealed class VoiceAIVoice
    {
        public string VoiceId { get; set; } = null!;
        public string? Name { get; set; }
        public string? Status { get; set; }
        public string? Visibility { get; set; }
    }
}
