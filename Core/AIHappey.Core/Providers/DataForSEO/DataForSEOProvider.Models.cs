using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.DataForSEO;

public partial class DataForSEOProvider
{
    private static readonly string[] Endpoints =
    [
        "chat_gpt",
        "claude",
        "gemini",
        "perplexity"
    ];

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return [];

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                var models = new List<Model>();

                foreach (var endpoint in Endpoints)
                {
                    using var req = new HttpRequestMessage(
                        HttpMethod.Get,
                        $"v3/ai_optimization/{endpoint}/llm_responses/models");

                    using var resp = await _client.SendAsync(req, ct);

                    if (!resp.IsSuccessStatusCode)
                        continue;

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    var root = doc.RootElement;

                    if (!root.TryGetProperty("tasks", out var tasksEl) ||
                        tasksEl.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var task in tasksEl.EnumerateArray())
                    {
                        string provider = endpoint;

                        if (task.TryGetProperty("data", out var dataEl) &&
                            dataEl.TryGetProperty("se", out var seEl))
                        {
                            provider = seEl.GetString() ?? endpoint;
                        }

                        if (!task.TryGetProperty("result", out var resultEl) ||
                            resultEl.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var res in resultEl.EnumerateArray())
                        {
                            if (!res.TryGetProperty("model_name", out var nameEl))
                                continue;

                            var name = nameEl.GetString();

                            if (string.IsNullOrWhiteSpace(name))
                                continue;

                            models.Add(new Model
                            {
                                Id = name.ToModelId(GetIdentifier()),
                                Name = name,
                                OwnedBy = provider
                            });
                        }
                    }
                }

                // remove duplicates
                return models
                    .GroupBy(m => m.Id)
                    .Select(g => g.First())
                    .ToList();
            },
            baseTtl: TimeSpan.FromHours(6),
            jitterMinutes: 720,
            cancellationToken: cancellationToken);
    }
}