using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Vogent;

public partial class VogentProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                using var req = new HttpRequestMessage(HttpMethod.Get, "api/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"Vogent API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;


                var arr = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                        ? dataEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    Model model = new();

                    if (el.TryGetProperty("id", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    if (el.TryGetProperty("name", out var orgEl))
                        model.Name = orgEl.GetString() ?? model.Name;

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                models.Add(new Model
                {
                    Id = BaseSpeechModel.ToModelId(GetIdentifier()),
                    Name = BaseSpeechModel,
                    OwnedBy = ProviderName,
                    Type = "speech",
                    Description = $"{ProviderName} base TTS model. Voice may be supplied via request.voice, providerOptions.{GetIdentifier()}.voiceId, or model id.",
                    Tags = ["tts", $"model:{BaseSpeechModel}", "base"]
                });

                var voices = await GetVoicesAsync(cancellationToken);
                models.AddRange(BuildDynamicVoiceModels(voices));

                return models
                    .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                    .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<VogentVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        var voices = new List<VogentVoice>();
        string? cursor = null;

        do
        {
            var path = "api/voices";
            if (!string.IsNullOrWhiteSpace(cursor))
                path += $"?cursor={Uri.EscapeDataString(cursor)}";

            using var resp = await _client.GetAsync(path, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{ProviderName} voices list failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("voices", out var voicesEl) && voicesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in voicesEl.EnumerateArray())
                {
                    var voiceId = item.TryGetProperty("id", out var idEl) ? idEl.GetString()?.Trim() : null;
                    if (string.IsNullOrWhiteSpace(voiceId))
                        continue;

                    voices.Add(new VogentVoice
                    {
                        Id = voiceId,
                        Name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null,
                        VoiceType = item.TryGetProperty("voiceType", out var voiceTypeEl) ? voiceTypeEl.GetString() : null,
                        VoiceTier = item.TryGetProperty("voiceTier", out var voiceTierEl) ? voiceTierEl.GetString() : null,
                        Description = item.TryGetProperty("description", out var descriptionEl) ? descriptionEl.GetString() : null,
                    });
                }
            }

            cursor = root.TryGetProperty("cursor", out var cursorEl) && cursorEl.ValueKind == JsonValueKind.String
                ? cursorEl.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        return voices
            .GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    private static IEnumerable<Model> BuildDynamicVoiceModels(IEnumerable<VogentVoice> voices)
        => voices
            .Where(v => !string.IsNullOrWhiteSpace(v.Id))
            .Select(v => new Model
            {
                Id = $"{BaseSpeechModel}/{v.Id}".ToModelId(nameof(Vogent).ToLowerInvariant()),
                OwnedBy = ProviderName,
                Type = "speech",
                Name = $"{BaseSpeechModel} · {BuildVoiceDisplayName(v)}",
                Description = string.IsNullOrWhiteSpace(v.Description)
                    ? $"{ProviderName} voice '{v.Id}'."
                    : v.Description,
                Tags = BuildVoiceTags(v)
            });

    private static string BuildVoiceDisplayName(VogentVoice voice)
    {
        var name = string.IsNullOrWhiteSpace(voice.Name) ? voice.Id : voice.Name.Trim();
        if (string.IsNullOrWhiteSpace(voice.VoiceType))
            return name;

        return $"{name} ({voice.VoiceType})";
    }

    private static IEnumerable<string> BuildVoiceTags(VogentVoice voice)
    {
        var tags = new List<string>
        {
            "tts",
            $"model:{BaseSpeechModel}",
            $"voice:{voice.Id}"
        };

        if (!string.IsNullOrWhiteSpace(voice.VoiceType))
            tags.Add($"voice_type:{voice.VoiceType}");

        if (!string.IsNullOrWhiteSpace(voice.VoiceTier))
            tags.Add($"voice_tier:{voice.VoiceTier}");

        return tags;
    }

    private sealed class VogentVoice
    {
        public string Id { get; set; } = null!;
        public string? Name { get; set; }
        public string? VoiceType { get; set; }
        public string? VoiceTier { get; set; }
        public string? Description { get; set; }
    }
}
