using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Cohere;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Cohere;

public partial class CohereProvider
{
    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        var url = "v2/rerank";
        ApplyAuthHeader();
        var now = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<CohereRerankingProviderMetadata>(GetIdentifier());
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            ["documents"] = request.Documents.Values,
            ["top_n"] = request.TopN
        };

        if (metadata?.Priority is not null)
        {
            payload["priority"] = metadata.Priority;
        }

        if (metadata?.MaxTokensPerDoc is not null)
        {
            payload["max_tokens_per_doc"] = metadata.MaxTokensPerDoc;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts),
                Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var errText = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            throw new Exception(errText);
        }

        using var doc = JsonDocument.Parse(errText);
        var root = doc.RootElement;

        var rerankModel = await this.GetModel(request.Model, cancellationToken);

        var results = root.TryGetProperty("results", out var resultsEl)
            && resultsEl.ValueKind == JsonValueKind.Array
                ? resultsEl.EnumerateArray()
                    .Select(r => new RerankingRanking
                    {
                        Index = r.GetProperty("index").GetInt32(),
                        RelevanceScore = (float)r.GetProperty("relevance_score").GetDouble(),
                    })
                    .ToList()
                : [];

        decimal? searchUnits = null;

        if (root.TryGetProperty("meta", out var metaEl)
            && metaEl.ValueKind == JsonValueKind.Object
            && metaEl.TryGetProperty("billed_units", out var billedUnitsEl)
            && billedUnitsEl.ValueKind == JsonValueKind.Object
            && billedUnitsEl.TryGetProperty("search_units", out var searchUnitsEl)
            && searchUnitsEl.ValueKind == JsonValueKind.Number
            && searchUnitsEl.TryGetDecimal(out var parsedSearchUnits))
        {
            searchUnits = parsedSearchUnits;
        }

        decimal? cost = null;

        if (searchUnits is not null && rerankModel?.Pricing?.Input is not null)
        {
            cost = searchUnits.Value * rerankModel.Pricing.Input;
        }

        return new RerankingResponse
        {
            Ranking = results,
            ProviderMetadata = GetIdentifier()
            .CreatePrimitiveProviderMetadata(
                root.TryGetProperty("meta", out var cohereMetaEl)
                && cohereMetaEl.ValueKind == JsonValueKind.Object ? new
                {
                    meta = cohereMetaEl.Clone()
                } : null,
                cost
            ),
            Response = new()
            {
                Timestamp = now,
                Id = root.TryGetId(),
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };

    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
