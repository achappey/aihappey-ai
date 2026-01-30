using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Cirrascale;

public partial class CirrascaleProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v2/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Cirrascale API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var models = new List<Model>();
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            return models;

        foreach (var category in root.EnumerateObject())
        {
            var type = category.Name; 

            if (category.Value.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var modelEl in category.Value.EnumerateArray())
            {
                if (modelEl.ValueKind != JsonValueKind.String)
                    continue;

                var rawId = modelEl.GetString();
                if (string.IsNullOrEmpty(rawId))
                    continue;

                var finalType = type == "llm" ? "language" : type == "text_to_image" ? "image" : null;

                if (finalType != null)
                {
                    models.Add(new Model
                    {
                        Id = rawId.ToModelId(GetIdentifier()),
                        Name = rawId,
                        Type = finalType,
                        OwnedBy = "Cirrascale",
                        Object = "model"
                    });
                }

            }
        }

        models.Add(new Model
        {
            Id = "BAAI/bge-reranker-base".ToModelId(GetIdentifier()),
            Name = "BAAI/bge-reranker-base",
            Type = "reranking",
            OwnedBy = "Cirrascale",
            Object = "model"
        });


        return models;
    }

}