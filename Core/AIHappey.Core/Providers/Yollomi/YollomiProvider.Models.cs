using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Yollomi;

public partial class YollomiProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {

        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"Yollomi API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                var images = root.TryGetProperty("image", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                        ? dataEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                foreach (var el in images)
                {
                    Model model = new()
                    {
                        Type = "image"
                    };

                    if (el.TryGetProperty("modelId", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }


                var videos = root.TryGetProperty("video", out var videoEl) && videoEl.ValueKind == JsonValueKind.Array
                       ? videoEl.EnumerateArray()
                       : Enumerable.Empty<JsonElement>();

                foreach (var el in videos)
                {
                    Model model = new()
                    {
                        Type = "video"
                    };

                    if (el.TryGetProperty("modelId", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    if (el.TryGetProperty("displayName", out var nameEl))
                    {
                        model.Name = nameEl.GetString() ?? model.Name;
                    }

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }


                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}