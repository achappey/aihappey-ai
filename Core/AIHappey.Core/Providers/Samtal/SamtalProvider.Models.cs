using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Samtal;

public partial class SamtalProvider
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

                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"Samtal API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                models.AddRange(GetIdentifier().GetModels());

                var root = doc.RootElement;

                // ✅ root is already an array
                var arr = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray()
                    : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                        ? dataEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    Model model = new();

                    if (el.TryGetProperty("model_id", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    if (el.TryGetProperty("name", out var nameEl))
                        model.Name = nameEl.GetString() ?? model.Name;

                    if (el.TryGetProperty("can_do_text_to_speech", out var ttsEl)
                           && ttsEl.ValueKind == JsonValueKind.True)
                    {
                        model.Type = "speech";
                    }
                    else
                    {
                        model.Type = "transcription";
                    }

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                await AddSamtalSpeechVoiceModelsAsync(models, cancellationToken);

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task AddSamtalSpeechVoiceModelsAsync(List<Model> models, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/voices");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
            return;

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = doc.RootElement;
        if (!root.TryGetProperty("voices", out var voicesEl) || voicesEl.ValueKind != JsonValueKind.Array)
            return;

        var speechModels = models
            .Where(model => string.Equals(model.Type, "speech", StringComparison.OrdinalIgnoreCase))
            .Select(model => model.Id?.Split('/', 2).Length == 2 ? model.Id.Split('/', 2)[1] : model.Name)
            .Where(modelId => !string.IsNullOrWhiteSpace(modelId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty("spectra-v1")
            .ToArray();

        foreach (var voiceEl in voicesEl.EnumerateArray())
        {
            if (voiceEl.ValueKind != JsonValueKind.Object)
                continue;

            var voiceId = TryGetSamtalVoiceString(voiceEl, "voice_id", "voiceId", "id");
            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            var name = TryGetSamtalVoiceString(voiceEl, "name") ?? voiceId;
            var category = TryGetSamtalVoiceString(voiceEl, "category");
            var description = TryGetSamtalVoiceString(voiceEl, "description");
            var labels = voiceEl.TryGetProperty("labels", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Object
                ? labelsEl
                : default;
            var language = TryGetSamtalVoiceString(labels, "language");
            var gender = TryGetSamtalVoiceString(labels, "gender");

            foreach (var speechModel in speechModels)
            {
                var shortcut = $"{speechModel}/{voiceId}";
                AddSamtalModelIfMissing(models, new Model
                {
                    Id = shortcut.ToModelId(GetIdentifier()),
                    Name = $"{speechModel}/{voiceId}",
                    Type = "speech",
                    OwnedBy = nameof(Samtal),
                    Description = BuildSamtalVoiceDescription(speechModel!, voiceId, name, description, language, gender),
                    Tags = BuildSamtalVoiceTags(category, language, gender)
                });
            }
        }
    }

    private static string BuildSamtalVoiceDescription(
        string modelId,
        string voiceId,
        string name,
        string? description,
        string? language,
        string? gender)
    {
        if (!string.IsNullOrWhiteSpace(description))
            return $"Samtal text-to-speech shortcut for model '{modelId}' using {name} ({voiceId}): {description.Trim()}";

        var details = new[] { language, gender }
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim());
        var suffix = details.Any() ? $" ({string.Join(", ", details)})" : string.Empty;
        return $"Samtal text-to-speech shortcut for model '{modelId}' using {name} ({voiceId}){suffix}.";
    }

    private static string[]? BuildSamtalVoiceTags(string? category, string? language, string? gender)
    {
        var tags = new List<string> { "voice" };

        if (!string.IsNullOrWhiteSpace(language))
            tags.Add(language.Trim().NormalizeLanguageCode());
        if (!string.IsNullOrWhiteSpace(gender))
            tags.Add(gender.Trim());

        return [.. tags.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string? TryGetSamtalVoiceString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String)
                {
                    return prop.Value.GetString();
                }
            }
        }

        return null;
    }

    private static void AddSamtalModelIfMissing(List<Model> models, Model model)
    {
        if (models.Any(existing => string.Equals(existing.Id, model.Id, StringComparison.OrdinalIgnoreCase)))
            return;

        models.Add(model);
    }
}
