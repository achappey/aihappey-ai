using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.NanoGPT;

public partial class NanoGPTProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var models = new List<Model>();
                await AddNanoGPTModelsAsync(models, "v1/models?detailed=true", null, null, ct);
                await AddNanoGPTModelsAsync(models, "v1/images/models", "image", null, ct);
                await AddNanoGPTModelsAsync(models, "v1/audio-models?detailed=true&type=all", null, "audio", ct);
                await AddNanoGPTModelsAsync(models, "v1/video-models?detailed=true", "video", null, ct);

                return models.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task AddNanoGPTModelsAsync(List<Model> models, string path, string? fixedType,
        string? family, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        using var resp = await _client.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"NanoGPT model discovery failed for '{path}': {err}");
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;
        foreach (var element in data.EnumerateArray())
        {
            var id = element.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var model = new Model
            {
                Id = id.ToModelId(GetIdentifier()), Name = element.TryGetProperty("name", out var name) ? name.GetString() ?? id : id,
                Description = element.TryGetProperty("description", out var description) ? description.GetString() : null,
                OwnedBy = element.TryGetProperty("owned_by", out var ownedBy) ? ownedBy.GetString() ?? "nanogpt" : "nanogpt",
                Created = element.TryGetProperty("created", out var created) && created.TryGetInt64(out var createdValue) ? createdValue : null,
                Type = fixedType ?? ResolveNanoGPTModelType(element, id, family)
            };
            models.Add(model);
        }
    }

    private static string ResolveNanoGPTModelType(JsonElement element, string id, string? family)
    {
        if (family == "audio" && element.TryGetProperty("capabilities", out var capabilities))
        {
            if (capabilities.TryGetProperty("text_to_speech", out var tts) && tts.ValueKind == JsonValueKind.True) return "speech";
            if (capabilities.TryGetProperty("speech_to_text", out var stt) && stt.ValueKind == JsonValueKind.True) return "transcription";
        }
        var normalized = id.ToLowerInvariant();
        if (normalized.Contains("whisper") || normalized.Contains("transcrib") || normalized.Contains("stt") || normalized.Contains("wizper")) return "transcription";
        if (normalized.Contains("tts") || normalized.Contains("speech") || normalized.Contains("elevenlabs") || normalized.Contains("music")) return "speech";
        return "language";
    }
}
