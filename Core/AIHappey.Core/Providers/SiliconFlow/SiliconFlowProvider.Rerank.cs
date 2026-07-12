using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.SiliconFlow;

public partial class SiliconFlowProvider
{
    public async Task<RerankingResponse> RerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
    {
        var url = "v1/rerank";

        ApplyAuthHeader();

        var now = DateTime.UtcNow;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            ["documents"] = request.Documents.Values,
            ["top_n"] = request.TopN
        };

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

        var tokens = root.TryGetProperty("tokens", out var tokensEl)
            && tokensEl.ValueKind == JsonValueKind.Object
                ? tokensEl.Clone()
                : (JsonElement?)null;

        return new RerankingResponse
        {
            Ranking = results,
            ProviderMetadata = GetIdentifier()
                .CreatePrimitiveProviderMetadata(tokens != null ? new
                {
                    tokens
                } : null),
            Response = new()
            {
                Timestamp = now,
                Id = root.TryGetId(),
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
