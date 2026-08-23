using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Zenlayer;

public partial class ZenlayerProvider
{
    public async Task<RerankingResponse> RerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRerankingRequest(request);

        var payload = CreateVercelPayload(
            request.ProviderOptions,
            GetIdentifier(),
            "model", "query", "documents", "top_n", "topN");
        var model = NormalizeZenlayerModelId(request.Model);
        payload["model"] = model;
        payload["query"] = request.Query;
        payload["documents"] = JsonNode.Parse(request.Documents.Values.GetRawText());
        if (request.TopN is not null)
            payload["top_n"] = request.TopN.Value;

        var result = await SendJsonAsync(
            HttpMethod.Post,
            "v2/rerank",
            payload,
            "rerank request",
            cancellationToken);

        var ranking = new List<RerankingRanking>();
        if (result.Root.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out var index)
                    || !item.TryGetProperty("relevance_score", out var score))
                    continue;

                ranking.Add(new RerankingRanking
                {
                    Index = index.GetInt32(),
                    RelevanceScore = score.GetSingle()
                });
            }
        }

        return new RerankingResponse
        {
            Ranking = ranking,
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root.Clone()),
            Response = new RerankResponseData
            {
                Id = result.Root.TryGetProperty("id", out var id) ? id.GetString() : null,
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = model.ToModelId(GetIdentifier()),
                Body = result.Root.Clone()
            }
        };
    }

    private static void ValidateRerankingRequest(RerankingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request));
        if (request.Documents is null || request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));
        if (request.Documents.Values.GetArrayLength() == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));
        if (request.TopN is < 0)
            throw new ArgumentException("TopN must be greater than or equal to zero.", nameof(request));
    }
}
