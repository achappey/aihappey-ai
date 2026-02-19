using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.AmazonBedrock;

public partial class AmazonBedrockProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);


        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "foundation-models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"AmazonBedrock API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();
        var root = doc.RootElement;

        var arr = root.TryGetProperty("modelSummaries", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                ? dataEl.EnumerateArray()
                : Enumerable.Empty<JsonElement>();

        foreach (var el in arr)
        {
            Model model = new();

            if (el.TryGetProperty("modelId", out var idEl))
            {
                model.Id = idEl.GetString()?.ToModelId(GetIdentifier()) ?? "";
                model.Name = idEl.GetString() ?? "";
            }

            if (el.TryGetProperty("modelName", out var nameEl))
                model.Name = nameEl.GetString() ?? model.Id;

            if (el.TryGetProperty("providerName", out var orgEl))
                model.OwnedBy = orgEl.GetString() ?? "";

            if (!string.IsNullOrEmpty(model.Id))
                models.Add(model);
        }

        return models;
    }
}