using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Jina;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Jina;

public partial class JinaProvider
{
    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        var url = "https://api.jina.ai/v1/rerank";

        ApplyAuthHeader();

        var now = DateTime.UtcNow;

        var metadata = request.GetProviderMetadata<JinaRerankingProviderMetadata>(GetIdentifier());
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            ["documents"] = request.Documents.Values,
            ["top_n"] = request.TopN
        };

        if (metadata?.ReturnDocuments is not null)
        {
            payload["return_documents"] = metadata.ReturnDocuments;
        }

        if (metadata?.ReturnEmbeddings is not null)
        {
            payload["return_embeddings"] = metadata.ReturnEmbeddings;
        }

        if (metadata?.MaxDocLength is not null)
        {
            payload["max_doc_length"] = metadata.MaxDocLength;
        }

        if (metadata?.Truncation is not null)
        {
            payload["truncation"] = metadata.Truncation;
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

        return new RerankingResponse
        {
            Ranking = results,
            Response = new ResponseData()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = errText
            }
        };

    }
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
