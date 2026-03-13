using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.JKAIHub;

public partial class JKAIHubProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var models = new List<Model>();
                string? url = "v1/models";

                while (!string.IsNullOrEmpty(url))
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    using var resp = await _client.SendAsync(req, cancellationToken);

                    if (!resp.IsSuccessStatusCode)
                    {
                        var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                        throw new Exception($"JKAIHub API error: {err}");
                    }

                    await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                    var root = doc.RootElement;

                    if (root.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            Model model = new();

                            if (el.TryGetProperty("jk_model_id", out var idEl))
                            {
                                model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                                model.Name = idEl.GetString() ?? "";
                            }

                            if (el.TryGetProperty("provider_display", out var orgEl))
                                model.OwnedBy = orgEl.GetString() ?? "";

                            if (!string.IsNullOrEmpty(model.Id))
                                models.Add(model);
                        }
                    }

                    url = root.TryGetProperty("next", out var nextEl) && nextEl.ValueKind == JsonValueKind.String
                        ? nextEl.GetString()
                        : null;
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}