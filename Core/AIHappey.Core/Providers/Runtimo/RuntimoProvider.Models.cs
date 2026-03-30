using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Globalization;

namespace AIHappey.Core.Providers.Runtimo;

public partial class RuntimoProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {

        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {

                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models?limit=10000");
                using var resp = await _client.SendAsync(req, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(cancellationToken);
                    throw new Exception($"Runtimo API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr = root.TryGetProperty("models", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                        ? dataEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    Model model = new();

                    if (el.TryGetProperty("endpoint_id", out var idEl))
                    {
                        model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                        model.Name = idEl.GetString() ?? "";
                    }

                    if (el.TryGetProperty("metadata", out var meta))
                    {
                        if (meta.TryGetProperty("display_name", out var nameEl))
                            model.Name = nameEl.GetString() ?? model.Name;

                        if (meta.TryGetProperty("description", out var descEl))
                            model.Description = descEl.GetString();

                        if (meta.TryGetProperty("category", out var catEl))
                        {
                            var category = catEl.GetString();

                            if (category?.EndsWith("to-image", StringComparison.OrdinalIgnoreCase) == true)
                                model.Type = "image";

                            else if (category?.EndsWith("to-video", StringComparison.OrdinalIgnoreCase) == true)
                                model.Type = "video";
                        }
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