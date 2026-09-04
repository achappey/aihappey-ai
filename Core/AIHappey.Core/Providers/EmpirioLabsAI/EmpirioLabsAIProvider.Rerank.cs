using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EmpirioLabsAI;

public partial class EmpirioLabsAIProvider
{
    public async Task<RerankingResponse> RerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query)) throw new ArgumentException("Query is required.", nameof(request));
        if (request.Documents is null || request.Documents.Values.ValueKind != JsonValueKind.Array
            || request.Documents.Values.GetArrayLength() == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));

        var payload = CreateEmpirioVercelPayload(request.ProviderOptions,
            "model", "query", "documents", "top_n", "topN");
        payload["model"] = request.Model;
        payload["query"] = request.Query;
        payload["documents"] = JsonNode.Parse(request.Documents.Values.GetRawText());
        SetEmpirio(payload, "top_n", request.TopN);

        var result = await SendEmpirioJsonAsync(HttpMethod.Post, "v1/reranks", payload, "rerank request", cancellationToken);
        var ranking = new List<RerankingRanking>();
        if (result.Root.TryGetProperty("output", out var output)
            && output.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out var index) || !index.TryGetInt32(out var indexValue)
                    || !item.TryGetProperty("relevance_score", out var score) || !score.TryGetSingle(out var scoreValue)) continue;
                ranking.Add(new RerankingRanking { Index = indexValue, RelevanceScore = scoreValue });
            }
        }

        return new RerankingResponse
        {
            Ranking = ranking,
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new RerankResponseData
            {
                Id = GetEmpirioString(result.Root, "request_id"),
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = result.Root
            }
        };
    }
}
