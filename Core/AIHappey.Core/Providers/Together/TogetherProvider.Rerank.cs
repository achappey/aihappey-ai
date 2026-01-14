using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Together;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.Together;

public partial class TogetherProvider : IModelProvider
{
    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        var url = "v1/rerank";

        ApplyAuthHeader();

        var now = DateTime.UtcNow;

        var metadata = request.GetProviderMetadata<TogetherRerankingProviderMetadata>(GetIdentifier());


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

        if (metadata?.RankFields is not null && metadata.RankFields.Any())
        {
            payload["rank_fields"] = metadata.RankFields;
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

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
