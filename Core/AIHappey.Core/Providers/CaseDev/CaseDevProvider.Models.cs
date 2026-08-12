using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.CaseDev;

public partial class CaseDevProvider
{
    private const string CaseDevTranscriptionModelId = "universal-3-pro";
    private static readonly string[] CaseDevSpeechModelIds =
    [
        "eleven_monolingual_v1",
        "eleven_multilingual_v1",
        "eleven_multilingual_v2",
        "eleven_turbo_v2"
    ];
    private static readonly string[] CaseDevNonStreamingOutputFormats =
    [
        "mp3_44100_128", "mp3_44100_192", "pcm_16000", "pcm_22050", "pcm_24000", "pcm_44100"
    ];
    private static readonly string[] CaseDevStreamingOutputFormats =
    [
        "mp3_44100_128", "mp3_22050_32", "pcm_16000", "pcm_22050", "pcm_24000", "pcm_44100"
    ];

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"CaseDev API error: {err}");
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

                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                models.AddRange(CaseDevSpeechModelIds.Select(modelId => new Model
                {
                    Id = modelId.ToModelId(GetIdentifier()),
                    Name = modelId,
                    OwnedBy = "case.dev",
                    Type = "speech",
                    Description = string.Equals(modelId, "eleven_multilingual_v1", StringComparison.Ordinal)
                        ? "Case.dev ElevenLabs text-to-speech synthesis (streaming only). Supply voice or use a voice-expanded model slug."
                        : "Case.dev ElevenLabs text-to-speech synthesis. Supply voice or use a voice-expanded model slug."
                }));

                models.Add(new Model
                {
                    Id = CaseDevTranscriptionModelId.ToModelId(GetIdentifier()),
                    Name = CaseDevTranscriptionModelId,
                    OwnedBy = "case.dev",
                    Type = "transcription",
                    Description = "Case.dev asynchronous speech-to-text transcription."
                });

                models.AddRange(await ListCaseDevVoiceModelsAsync(ct));

                return models
                    .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IEnumerable<Model>> ListCaseDevVoiceModelsAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync("/voice/v1/voices", cancellationToken);
        if (!response.IsSuccessStatusCode)
            return [];

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var voices = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("voices", out var voicesElement) && voicesElement.ValueKind == JsonValueKind.Array
                ? voicesElement.EnumerateArray()
                : root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array
                    ? dataElement.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();

        var models = new List<Model>();
        foreach (var voice in voices)
        {
            var voiceId = ReadCaseDevVoiceString(voice, "voice_id")
                ?? ReadCaseDevVoiceString(voice, "voiceId")
                ?? ReadCaseDevVoiceString(voice, "id");
            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            var name = ReadCaseDevVoiceString(voice, "name") ?? voiceId;
            var description = ReadCaseDevVoiceString(voice, "description");
            models.AddRange(CaseDevSpeechModelIds.Select(modelId => new Model
            {
                Id = $"{modelId}/{voiceId}".ToModelId(GetIdentifier()),
                Name = $"{modelId}/{name}",
                OwnedBy = "case.dev",
                Type = "speech",
                Description = string.IsNullOrWhiteSpace(description)
                    ? $"Case.dev ElevenLabs model '{modelId}' with voice '{name}'"
                    : $"Case.dev ElevenLabs model '{modelId}' with voice '{name}'. {description}",
                Tags = ["voice"]
            }));
        }

        return models;
    }

    private static string? ReadCaseDevVoiceString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
