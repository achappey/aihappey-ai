using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.Cohere;

public partial class CohereProvider
{
    public async Task<IEnumerable<Model>> ListModels(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models?page_size=1000");
        using var response = await _client.SendAsync(request, cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("models", out var modelsEl)
            || modelsEl.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<Model>();

        foreach (var item in modelsEl.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name)) continue;

            DateTimeOffset? createdAt = null;
            if (item.TryGetProperty("created_at", out var createdEl)
                && createdEl.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(createdEl.GetString(), out var dt))
            {
                createdAt = dt;
            }

            result.Add(new Model
            {
                Id = name!.ToModelId(GetIdentifier()),
                Name = name!,
                OwnedBy = nameof(Cohere),
                Created = createdAt?.ToUnixTimeSeconds()
            });
        }

        return result;
    }
}