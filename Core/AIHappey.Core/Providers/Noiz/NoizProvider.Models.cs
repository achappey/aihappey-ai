using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Noiz;

public partial class NoizProvider
{
    private const string ProviderName = "Noiz";
    private const string BaseSpeechModel = "text-to-speech";

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
                Description = $"{ProviderName} base TTS model. Voice may be supplied via request.voice, providerOptions.noiz.voice_id, or model id.",
                Tags = ["tts", $"model:{BaseSpeechModel}", "base"]
            }
        };

        var voices = await GetVoicesAsync(cancellationToken);
        models.AddRange(BuildDynamicVoiceModels(voices));

        return [.. models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private async Task<IReadOnlyList<NoizVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        var voices = new List<NoizVoice>();
        voices.AddRange(await GetVoicesByTypeAsync("custom", cancellationToken));
        voices.AddRange(await GetVoicesByTypeAsync("built-in", cancellationToken));

        return [.. voices
            .Where(IsValidVoice)
            .GroupBy(v => v.VoiceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())];
    }

    private async Task<IReadOnlyList<NoizVoice>> GetVoicesByTypeAsync(string voiceType, CancellationToken cancellationToken)
    {
        const int limit = 100;
        var skip = 0;
        var totalCount = int.MaxValue;
        var voices = new List<NoizVoice>();

        while (skip < totalCount)
        {
            var path = $"voices?voice_type={Uri.EscapeDataString(voiceType)}&skip={skip}&limit={limit}";
            using var resp = await _client.GetAsync(path, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}) for type '{voiceType}': {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var parsed = ParseVoices(root, voiceType, out var pageTotal);
            voices.AddRange(parsed);

            totalCount = pageTotal ?? voices.Count;
            if (parsed.Count == 0)
                break;

            skip++;
        }

        return voices;
    }

    private IEnumerable<Model> BuildDynamicVoiceModels(IEnumerable<NoizVoice> voices)
        => voices
            .Where(IsValidVoice)
            .Select(v => new Model
            {
                Id = $"{BaseSpeechModel}/{v.VoiceId}".ToModelId(GetIdentifier()),
                OwnedBy = ProviderName,
                Type = "speech",
                Name = $"{BaseSpeechModel} · {BuildVoiceDisplayName(v)}",
                Description = $"{ProviderName} voice '{v.VoiceId}' ({v.VoiceType ?? "unknown"}).",
                Tags = BuildVoiceTags(v)
            });

    private static IReadOnlyList<NoizVoice> ParseVoices(JsonElement root, string fallbackVoiceType, out int? totalCount)
    {
        totalCount = null;

        if (!TryGetPropertyIgnoreCase(root, "data", out var data) || data.ValueKind != JsonValueKind.Object)
            return [];

        totalCount = ReadInt(data, "total_count");

        if (!TryGetPropertyIgnoreCase(data, "voices", out var voicesEl) || voicesEl.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<NoizVoice>();
        foreach (var item in voicesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var voiceId = ReadString(item, "voice_id")?.Trim();
            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            voices.Add(new NoizVoice
            {
                VoiceId = voiceId,
                DisplayName = ReadString(item, "display_name"),
                VoiceType = ReadString(item, "voice_type") ?? fallbackVoiceType,
                Labels = ReadString(item, "labels")
            });
        }

        return voices;
    }

    private static string BuildVoiceDisplayName(NoizVoice voice)
    {
        var name = string.IsNullOrWhiteSpace(voice.DisplayName) ? voice.VoiceId : voice.DisplayName.Trim();
        if (string.IsNullOrWhiteSpace(voice.VoiceType))
            return name;

        return $"{name} ({voice.VoiceType})";
    }

    private static IEnumerable<string> BuildVoiceTags(NoizVoice voice)
    {
        var tags = new List<string>
        {
            "tts",
            $"model:{BaseSpeechModel}",
            $"voice:{voice.VoiceId}"
        };

        if (!string.IsNullOrWhiteSpace(voice.VoiceType))
            tags.Add($"voice_type:{voice.VoiceType}");

        if (!string.IsNullOrWhiteSpace(voice.Labels))
            tags.Add($"labels:{voice.Labels}");

        return tags;
    }

    private static bool IsValidVoice(NoizVoice voice)
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

    private static int? ReadInt(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i))
            return i;

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out i))
            return i;

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

    private sealed class NoizVoice
    {
        public string VoiceId { get; set; } = null!;
        public string? DisplayName { get; set; }
        public string? VoiceType { get; set; }
        public string? Labels { get; set; }
    }
}
