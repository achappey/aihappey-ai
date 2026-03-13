using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Fal;

public partial class FalProvider
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

                var models = new List<Model>();
                string? cursor = null;

                do
                {
                    var url = "v1/models";

                    if (!string.IsNullOrEmpty(cursor))
                        url += $"?cursor={cursor}";

                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    using var resp = await _client.SendAsync(req, cancellationToken);

                    if (!resp.IsSuccessStatusCode)
                    {
                        var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                        throw new Exception($"Fal API error: {err}");
                    }

                    await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                    var root = doc.RootElement;

                    if (root.TryGetProperty("models", out var dataEl) &&
                        dataEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in dataEl.EnumerateArray())
                        {
                            Model model = new();

                            if (el.TryGetProperty("endpoint_id", out var idEl))
                            {
                                var id = idEl.GetString();
                                if (!string.IsNullOrEmpty(id))
                                {
                                    model.Id = id.ToModelId(GetIdentifier());
                                    model.Name = id;
                                }
                            }

                            if (el.TryGetProperty("metadata", out var metaEl) &&
                                metaEl.ValueKind == JsonValueKind.Object)
                            {
                                if (metaEl.TryGetProperty("display_name", out var nameEl))
                                    model.Name = nameEl.GetString() ?? model.Name;
                                if (metaEl.TryGetProperty("description", out var descriptionEl))
                                    model.Description = descriptionEl.GetString() ?? string.Empty;
                            }


                            if (!string.IsNullOrEmpty(model.Id))
                                models.Add(model);
                        }
                    }

                    bool hasMore = root.TryGetProperty("has_more", out var hasMoreEl) &&
                                   hasMoreEl.GetBoolean();

                    cursor = root.TryGetProperty("next_cursor", out var cursorEl)
                        ? cursorEl.GetString()
                        : null;

                    if (!hasMore)
                        break;

                    await Task.Delay(TimeSpan.FromSeconds(0.5), cancellationToken);

                } while (!string.IsNullOrEmpty(cursor));

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}