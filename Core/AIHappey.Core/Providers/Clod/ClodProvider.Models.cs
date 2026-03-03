using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Clod;

public partial class ClodProvider
{
    public async Task<IEnumerable<Model>> ListModels(
     CancellationToken cancellationToken = default)
    {
        var cacheKey = $"models:{GetIdentifier()}";

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new Exception($"Clod API error: {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var result = new List<Model>();

                if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                    dataEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in dataEl.EnumerateArray())
                    {
                        var model = new Model();

                        if (el.TryGetProperty("id", out var idEl))
                        {
                            var id = idEl.GetString();
                            model.Id = id?.ToModelId(GetIdentifier()) ?? "";
                            model.Name = id ?? "";
                        }

                        if (el.TryGetProperty("owned_by", out var orgEl))
                            model.OwnedBy = orgEl.GetString() ?? "";

                        if (!string.IsNullOrEmpty(model.Id))
                            result.Add(model);
                    }
                }

                return result;
            },
            baseTtl: TimeSpan.FromMinutes(10),
            jitterMinutes: 5,
            cancellationToken: cancellationToken);
    }
}