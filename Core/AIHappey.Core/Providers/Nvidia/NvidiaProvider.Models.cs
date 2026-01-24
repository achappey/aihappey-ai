using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.Nvidia;

public partial class NvidiaProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"{nameof(Nvidia).ToUpperInvariant()} API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
            ? dataEl
            : default;

        if (data.ValueKind != JsonValueKind.Array)
            return [];

        return [.. data
            .EnumerateArray()
            .Select(m =>
            {
                var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var created = m.TryGetProperty("created", out var cEl) && cEl.ValueKind == JsonValueKind.Number
                    ? cEl.GetInt64()
                    : 0;
                var ownedBy = m.TryGetProperty("owned_by", out var oEl) ? oEl.GetString() : null;

                return new Model
                {
                    Id = (id ?? string.Empty).ToModelId(GetIdentifier()),
                    Name = id ?? string.Empty,
                    OwnedBy = ownedBy ?? nameof(Nvidia).ToUpperInvariant(),
                    Created = created,
                    Type = "language",
                };
            })
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .DistinctBy(m => m.Id)
            .OrderByDescending(m => m.Created)];
    }

}

