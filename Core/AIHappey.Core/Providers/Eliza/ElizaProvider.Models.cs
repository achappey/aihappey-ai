using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Eliza;

public partial class ElizaProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var models = new List<Model>();

                models.AddRange(await this.ListLanguageModels(cancellationToken));
                models.AddRange(await this.ListAgentModels(cancellationToken));
                models.AddRange(ListVoiceModels());

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IEnumerable<Model>> ListLanguageModels(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Eliza API error: {err}");
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

            model.ContextWindow = el.TryGetProperty("context_window", out var v) &&
                v.ValueKind == JsonValueKind.Number
                    ? v.GetInt32()
                    : null;

            if (el.TryGetProperty("max_tokens", out var maxTokensEl))
                model.MaxTokens = maxTokensEl.GetInt32();

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (el.TryGetProperty("name", out var nameEl))
                model.Name = nameEl.GetString() ?? model.Name;

            if (el.TryGetProperty("description", out var descriptionEl))
                model.Description = descriptionEl.GetString() ?? string.Empty;

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }

    private async Task<IEnumerable<Model>> ListAgentModels(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return [];

        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            "my-agents/characters?limit=1000");

        ApplyAuthHeader();

        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Eliza Agents API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();

        if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
            !dataEl.TryGetProperty("characters", out var charsEl) ||
            charsEl.ValueKind != JsonValueKind.Array)
            return models;

        foreach (var el in charsEl.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl)
                ? idEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(id))
                continue;

            var name = el.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString()
                : id;

            var description =
                el.TryGetProperty("bio", out var bioEl) &&
                bioEl.ValueKind == JsonValueKind.Array
                    ? string.Join("\n",
                        bioEl.EnumerateArray()
                            .Select(x => x.GetString())
                            .Where(x => !string.IsNullOrWhiteSpace(x)))
                    : string.Empty;

            models.Add(new Model
            {
                Id = id.ToModelId(GetIdentifier()),
                Name = name ?? id,
                Type = "language",
                OwnedBy = "Eliza",
                Description = description
            });
        }

        return models;
    }

    private IEnumerable<Model> ListVoiceModels()
    {
        yield return new Model
        {
            Id = "eleven_multilingual_v2".ToModelId(GetIdentifier()),
            Name = "Eleven Multilingual v2",
            Type = "speech",
            OwnedBy = "Eliza",
            Description = "Text-to-speech via Eliza Voice API. Requires a voice ID from Eliza voice/list."
        };

        yield return new Model
        {
            Id = "eleven_turbo_v2_5".ToModelId(GetIdentifier()),
            Name = "Eleven Turbo v2.5",
            Type = "speech",
            OwnedBy = "Eliza",
            Description = "Fast text-to-speech via Eliza Voice API. Requires a voice ID from Eliza voice/list."
        };

        yield return new Model
        {
            Id = "eleven_flash_v2_5".ToModelId(GetIdentifier()),
            Name = "Eleven Flash v2.5",
            Type = "speech",
            OwnedBy = "Eliza",
            Description = "Fast text-to-speech via Eliza Voice API. Requires a voice ID from Eliza voice/list."
        };

        yield return new Model
        {
            Id = "eleven_v3".ToModelId(GetIdentifier()),
            Name = "Eleven v3",
            Type = "speech",
            OwnedBy = "Eliza",
            Description = "High quality text-to-speech via Eliza Voice API. Requires a voice ID from Eliza voice/list."
        };

        yield return new Model
        {
            Id = "voice/stt".ToModelId(GetIdentifier()),
            Name = "Eliza Voice STT",
            Type = "transcription",
            OwnedBy = "Eliza",
            Description = "Speech-to-text via Eliza Voice API."
        };
    }
}
