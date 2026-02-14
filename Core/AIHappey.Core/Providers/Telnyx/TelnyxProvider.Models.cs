using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Telnyx;

public partial class TelnyxProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "ai/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Telnyx models failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);

        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<Model>();

        foreach (var el in data.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var fullId = id!.ToModelId(GetIdentifier());

            models.Add(new Model
            {
                Id = fullId,
                Name = id!,
                OwnedBy = el.TryGetProperty("owned_by", out var ob) ? (ob.GetString() ?? "") : "",
                Created = el.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number
                    ? created.GetInt64()
                    : null,
                Type = fullId.GuessModelType()
            });
        }

        models.AddRange(await this.ListModels(_keyResolver.Resolve(GetIdentifier())));

        return models;
    }

}

