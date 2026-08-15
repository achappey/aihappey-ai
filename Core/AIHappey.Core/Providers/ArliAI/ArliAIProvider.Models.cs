using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.ArliAI;

public partial class ArliAIProvider
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
                    throw new Exception($"ArliAI API error: {err}");
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


                    if (el.TryGetProperty("owned_by", out var orgEl))
                        model.OwnedBy = orgEl.GetString() ?? "";

                    if (!string.IsNullOrEmpty(model.Id))
                        models.Add(model);
                }

                using var imageReq = new HttpRequestMessage(HttpMethod.Get, "v1/models/image-models");
                using var imageResp = await _client.SendAsync(imageReq, cancellationToken);
                if (imageResp.IsSuccessStatusCode)
                {
                    await using var imageStream = await imageResp.Content.ReadAsStreamAsync(cancellationToken);
                    using var imageDoc = await JsonDocument.ParseAsync(imageStream, cancellationToken: cancellationToken);
                    foreach (var name in GetArliImageModelNames(imageDoc.RootElement))
                    {
                        if (models.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        models.Add(new Model
                        {
                            Id = name.ToModelId(GetIdentifier()),
                            Name = name,
                            OwnedBy = GetIdentifier(),
                            Type = "image"
                        });
                    }
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static IEnumerable<string> GetArliImageModelNames(JsonElement root)
    {
        var values = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray()
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
                    ? models.EnumerateArray()
                    : Enumerable.Empty<JsonElement>();

        foreach (var value in values)
        {
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                yield return value.GetString()!;
                continue;
            }

            if (value.ValueKind != JsonValueKind.Object) continue;
            foreach (var key in new[] { "name", "model_name", "title", "id" })
                if (value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString()))
                {
                    yield return property.GetString()!;
                    break;
                }
        }
    }
}
