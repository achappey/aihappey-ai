using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Nebius;

public sealed partial class NebiusProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        var payload = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Nebius API error: {payload}");

        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        List<Model> models = [];

        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            long? created = null;
            if (item.TryGetProperty("created", out var createdEl)
                && createdEl.ValueKind == JsonValueKind.Number
                && createdEl.TryGetInt64(out var epoch))
            {
                created = epoch;
            }

            var ownedBy = item.TryGetProperty("owned_by", out var ownedEl) ? ownedEl.GetString() : null;

            models.Add(new Model
            {
                Id = id!.ToModelId(GetIdentifier()),
                Name = id!,
                OwnedBy = nameof(Nebius),
                Created = created,
                Type = id!.GuessModelType(),
            });
        }

        return models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .DistinctBy(m => m.Id)
            .OrderByDescending(m => m.Created);
    }
}

