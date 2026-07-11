using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.WAYSCloud;

public partial class WAYSCloudProvider
{
    private const string ChatbotModelPrefix = "chatbot/";
    private const string TranscriptionModelId = "transcription";

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

                    var models = new List<Model>();

                    await AddChatCompletionModelsAsync(models, cancellationToken);
                    await AddChatbotModelsAsync(models, cancellationToken);

                    models.Add(new Model
                    {
                        Id = TranscriptionModelId.ToModelId(GetIdentifier()),
                        Name = "WAYSCloud Transcription",
                        Description = "WAYSCloud Speech Intelligence transcription job API.",
                        OwnedBy = nameof(WAYSCloud),
                        Type = "transcription"
                    });

                    return models.DistinctBy(model => model.Id).ToList();
                },
                baseTtl: TimeSpan.FromHours(4),
                jitterMinutes: 480,
                cancellationToken: cancellationToken);
    }

    private async Task AddChatCompletionModelsAsync(List<Model> models, CancellationToken cancellationToken)
    {

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"WAYSCloud API error: {err}");
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
            }

            if (el.TryGetProperty("owned_by", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }
    }

    private async Task AddChatbotModelsAsync(List<Model> models, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/chatbot/api/bots");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
            return;

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = doc.RootElement;
        if (!root.TryGetProperty("bots", out var botsEl) || botsEl.ValueKind != JsonValueKind.Array)
            return;

        foreach (var botEl in botsEl.EnumerateArray())
        {
            if (botEl.ValueKind != JsonValueKind.Object)
                continue;

            var slug = botEl.TryGetProperty("slug", out var slugEl) && slugEl.ValueKind == JsonValueKind.String
                ? slugEl.GetString()
                : null;
            var id = botEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;
            var botId = !string.IsNullOrWhiteSpace(slug) ? slug : id;

            if (string.IsNullOrWhiteSpace(botId))
                continue;

            var name = botEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString()
                : null;
            var status = botEl.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString()
                : null;
            var language = botEl.TryGetProperty("language", out var languageEl) && languageEl.ValueKind == JsonValueKind.String
                ? languageEl.GetString()
                : null;

            models.Add(new Model
            {
                Id = (ChatbotModelPrefix + botId).ToModelId(GetIdentifier()),
                Name = string.IsNullOrWhiteSpace(name) ? botId : name,
                Description = $"WAYSCloud chatbot{(string.IsNullOrWhiteSpace(slug) ? string.Empty : $" '{slug}'")}.",
                OwnedBy = nameof(WAYSCloud),
                Type = "language",
                Tags = ["persona", .. new[] { language }.Where(static value => !string.IsNullOrWhiteSpace(value))!]
            });
        }
    }
}
