using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.xAI;

public partial class XAIProvider : IModelProvider
{
    public string GetIdentifier() => XAIExtensions.XAIIdentifier;

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var response = await _client.SendAsync(request, cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<Model>();

        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;

            //     DateTimeOffset? createdAt = null;\
            long? createdAt = null;
            if (item.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number)
            {
                // xAI returns epoch seconds (per your example)
                if (createdEl.TryGetInt64(out var epoch))
                    createdAt = epoch;
            }

            models.Add(new Model
            {
                Id = id!.ToModelId(GetIdentifier()),
                Name = id!,
                Created = createdAt,
                //                Publisher = nameof(xAI),
                OwnedBy = nameof(xAI)
            });
        }

        // Keep only models this provider can actually handle
        return models;
    }


}