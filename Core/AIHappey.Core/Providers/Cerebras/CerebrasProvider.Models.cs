using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Cerebras;

public partial class CerebrasProvider 
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();
        var root = doc.RootElement;

        // ✅ root is already an array
        var arr = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                ? dataEl.EnumerateArray()
                : Enumerable.Empty<JsonElement>();

        foreach (var el in arr)
        {
            if (!el.TryGetProperty("id", out var idEl))
                continue;

            var id = idEl.GetString();
            if (string.IsNullOrEmpty(id))
                continue;

            var model = new Model
            {
                Id = id.ToModelId(GetIdentifier()),
                Name = id,
                Type = "language",
                OwnedBy = el.TryGetProperty("owned_by", out var ownedByEl)
                    ? ownedByEl.GetString() ?? ""
                    : "",
            };

            if (el.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number)
            {
                var unix = createdEl.GetInt64();
                model.Created = unix;
            }

            models.Add(model);
        }

        return models;
    }
}