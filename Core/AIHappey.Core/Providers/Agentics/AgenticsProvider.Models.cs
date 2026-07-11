using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Agentics;

public partial class AgenticsProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {

        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {

                var models = new List<Model>();

                await AddChatModelsAsync(models, cancellationToken);
                await AddImageModelsAsync(models, cancellationToken);

                models.AddRange(GetIdentifier().GetModels());
                await AddSpeechVoiceModelsAsync(models, cancellationToken);

                return models.DistinctBy(model => model.Id).ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task AddChatModelsAsync(List<Model> models, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Agentics API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

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
                model.Type = model.Name.GuessModelType();
            }

            model.ContextWindow = el.TryGetProperty("context_length", out var v) &&
                v.ValueKind == JsonValueKind.Number
                    ? v.GetInt32()
                    : null;

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";


            if (el.TryGetProperty("display_name", out var nameEL))
                model.Name = nameEL.GetString() ?? model.Name;

            if (el.TryGetProperty("description", out var descriptionEl))
                model.Description = descriptionEl.GetString() ?? string.Empty;

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }
    }

    private async Task AddImageModelsAsync(List<Model> models, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/images/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Agentics image models API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = doc.RootElement;

        var arr = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                ? dataEl.EnumerateArray()
                : Enumerable.Empty<JsonElement>();

        foreach (var el in arr)
        {
            var name = el.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var category = el.TryGetProperty("category", out var categoryEl)
                ? categoryEl.GetString()
                : null;
            var provider = el.TryGetProperty("provider", out var providerEl)
                ? providerEl.GetString()
                : null;

            models.Add(new Model
            {
                Id = name.ToModelId(GetIdentifier()),
                Name = name,
                Type = "image",
                OwnedBy = string.IsNullOrWhiteSpace(provider) ? nameof(Agentics) : provider,
                Description = el.TryGetProperty("description", out var descriptionEl)
                    ? descriptionEl.GetString() ?? string.Empty
                    : string.Empty
            });
        }
    }  

    private async Task AddSpeechVoiceModelsAsync(List<Model> models, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/audio/voices");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
            return;

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = doc.RootElement;
        if (!root.TryGetProperty("voices", out var voicesEl) || voicesEl.ValueKind != JsonValueKind.Array)
            return;

        var defaultVoice = root.TryGetProperty("defaultVoice", out var defaultVoiceEl) && defaultVoiceEl.ValueKind == JsonValueKind.String
            ? defaultVoiceEl.GetString()
            : null;

        foreach (var voiceEl in voicesEl.EnumerateArray())
        {
            if (voiceEl.ValueKind != JsonValueKind.Object)
                continue;

            var voiceId = voiceEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            var name = voiceEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? voiceId
                : voiceId;
            var gender = voiceEl.TryGetProperty("gender", out var genderEl) && genderEl.ValueKind == JsonValueKind.String
                ? genderEl.GetString()
                : null;

            models.Add(new Model
            {
                Id = $"{GetIdentifier()}/{AgenticsSpeechBaseModel}/{voiceId}",
                Name = $"{AgenticsSpeechBaseModel}/{voiceId}",
                Type = "speech",
                OwnedBy = nameof(Agentics),
                Description = string.Equals(voiceId, defaultVoice, StringComparison.OrdinalIgnoreCase)
                    ? $"Agentics text-to-speech shortcut model using the default {name} voice."
                    : $"Agentics text-to-speech shortcut model using the {name} voice.",
                Tags = BuildSpeechVoiceTags(gender, string.Equals(voiceId, defaultVoice, StringComparison.OrdinalIgnoreCase))
            });
        }
    }

    private static string[]? BuildSpeechVoiceTags(string? gender, bool isDefaultVoice)
    {
        var tags = new List<string>();

        if (!string.IsNullOrWhiteSpace(gender))
            tags.Add(gender);
        if (isDefaultVoice)
            tags.Add("default-voice");

        return tags.Count == 0 ? null : [.. tags];
    }
}
