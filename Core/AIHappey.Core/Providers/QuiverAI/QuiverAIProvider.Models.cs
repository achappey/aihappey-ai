using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.QuiverAI;

public partial class QuiverAIProvider
{
    private async Task<IEnumerable<Model>> ListModelsLiveAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"QuiverAI list models failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<Model>();
        foreach (var item in dataEl.EnumerateArray())
        {
            var rawId = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(rawId))
                continue;

            var type = item.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString()
                : null;

            models.Add(new Model
            {
                Id = rawId.ToModelId(GetIdentifier()),
                Name = item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? (nameEl.GetString() ?? rawId)
                    : rawId,
                Description = item.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
                    ? descEl.GetString()
                    : null,
                OwnedBy = item.TryGetProperty("owned_by", out var ownEl) && ownEl.ValueKind == JsonValueKind.String
                    ? (ownEl.GetString() ?? "")
                    : "",
                Created = item.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number
                    ? createdEl.GetInt64()
                    : null,
                Type = "image"
            });
        }

        return models;
    }
}

