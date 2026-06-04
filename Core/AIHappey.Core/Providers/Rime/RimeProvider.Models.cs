using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Rime;

public partial class RimeProvider
{
    private const string RimeModelPrefix = "rime/";
    private static readonly string[] BaseModels = ["mistv2", "mistv3", "arcana", "coda"];

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync<IEnumerable<Model>>(
            cacheKey,
            async ct =>
            {
                var models = new List<Model>(BuildBaseModels());
                var voices = await GetVoicesAsync(cancellationToken);
                models.AddRange(BuildDynamicVoiceModels(voices));

                return [.. models
                    .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())];

            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<RimeVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync("data/voices/all-v2.json", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return ParseVoices(doc.RootElement);
    }
    
    private static readonly Dictionary<string, string> BaseModelDescriptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["coda"] = "Our flagship TTS model. LLM backbone with a dedicated speech inference engine, trained on conversational full-duplex data.",
            ["arcana"] = "Previous-generation flagship TTS model: ultra-realistic, expressive voices with low latency and native multilingual code-switching across more than 10 languages.",
            ["mistv3"] = "Major update to the Mist engine: very low TTFA on the GPU engine, significantly faster than Coda or Arcana while preserving Mist’s pronunciation control and predictability.",
            ["mistv2"] = "Previous-generation Mist model. For new projects, prefer Mist v3."
        };

    private IEnumerable<Model> BuildBaseModels()
        => BaseModels.Select(modelId => new Model
        {
            Id = $"{RimeModelPrefix}{modelId}",
            OwnedBy = ProviderName,
            Type = "speech",
            Name = modelId,
            Description = BaseModelDescriptions.TryGetValue(modelId, out var description)
                ? description
                : null,
            Tags = [$"model:{modelId}", "tts", "base"]
        });

    private IEnumerable<Model> BuildDynamicVoiceModels(IEnumerable<RimeVoice> voices)
    {
        foreach (var voice in voices.Where(IsValidVoice))
        {
            yield return new Model
            {
                Id = $"{RimeModelPrefix}{voice.ModelId}/{voice.VoiceId}",
                OwnedBy = ProviderName,
                Type = "speech",
                Name = $"{voice.ModelId} / {voice.VoiceId} ({voice.Language})",
                Description = $"{ProviderName} voice '{voice.VoiceId}' on model '{voice.ModelId}' for language '{voice.Language}'.",
                Tags = BuildVoiceTags(voice)
            };
        }
    }

    private static IReadOnlyList<RimeVoice> ParseVoices(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return [];

        var voices = new List<RimeVoice>();
        foreach (var modelProp in root.EnumerateObject())
        {
            if (modelProp.Value.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var langProp in modelProp.Value.EnumerateObject())
            {
                if (langProp.Value.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var voiceEl in langProp.Value.EnumerateArray())
                {
                    if (voiceEl.ValueKind != JsonValueKind.String)
                        continue;

                    var voiceId = voiceEl.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(voiceId))
                        continue;

                    voices.Add(new RimeVoice
                    {
                        ModelId = modelProp.Name.Trim(),
                        Language = langProp.Name.Trim(),
                        VoiceId = voiceId
                    });
                }
            }
        }

        return voices;
    }

    private static bool IsValidVoice(RimeVoice voice)
        => !string.IsNullOrWhiteSpace(voice.ModelId)
            && !string.IsNullOrWhiteSpace(voice.Language)
            && !string.IsNullOrWhiteSpace(voice.VoiceId)
            && BaseModels.Contains(voice.ModelId, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> BuildVoiceTags(RimeVoice voice)
        =>
        [
            $"model:{voice.ModelId}",
            $"voice:{voice.VoiceId}",
            $"language:{voice.Language}",
            "tts"
        ];

    private sealed class RimeVoice
    {
        public string ModelId { get; set; } = null!;
        public string Language { get; set; } = null!;
        public string VoiceId { get; set; } = null!;
    }
}
