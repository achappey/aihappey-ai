using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.DeepSeek;

public sealed partial class DeepSeekProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
         if (string.IsNullOrWhiteSpace(keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);
            
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        var payload = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"DeepSeek API error: {payload}");

        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        List<Model> models = [];

        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var ownedBy = item.TryGetProperty("owned_by", out var ownedEl) ? ownedEl.GetString() : null;

            long? created = null;
            if (item.TryGetProperty("created", out var createdEl)
                && createdEl.ValueKind == JsonValueKind.Number
                && createdEl.TryGetInt64(out var epoch))
            {
                created = epoch;
            }

            models.Add(new Model
            {
                Id = id!.ToModelId(GetIdentifier()),
                Name = id!,
                OwnedBy = ownedBy ?? "DeepSeek",
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

