using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Gatewayz;

public partial class GatewayzProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var models = new List<Model>();

                int offset = 0;
                bool hasMore = true;

                while (hasMore)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"v1/models?offset={offset}&limit=1000");
                    using var resp = await _client.SendAsync(req, cancellationToken);

                    if (!resp.IsSuccessStatusCode)
                    {
                        var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                        throw new Exception($"Gatewayz API error: {err}");
                    }

                    await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                    var root = doc.RootElement;

                    if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in dataEl.EnumerateArray())
                        {
                            Model model = new();

                            if (el.TryGetProperty("id", out var idEl))
                            {
                                model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                                model.Name = idEl.GetString() ?? "";
                            }


                            if (el.TryGetProperty("description", out var orgEl))
                                model.Description = orgEl.GetString() ?? "";

                            if (el.TryGetProperty("name", out var nameEl))
                                model.Name = nameEl.GetString() ?? model.Name;

                            if (!string.IsNullOrEmpty(model.Id))
                                models.Add(model);
                        }
                    }

                    hasMore = root.TryGetProperty("has_more", out var hasMoreEl) && hasMoreEl.GetBoolean();

                    if (hasMore && root.TryGetProperty("next_offset", out var nextOffsetEl))
                        offset = nextOffsetEl.GetInt32();
                    else
                        break;
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}