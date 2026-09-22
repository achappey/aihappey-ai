using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.CanRouter;

public partial class CanRouterProvider
{
    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ApplyAuthHeader();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            ["documents"] = request.Documents.Values,
            ["top_n"] = request.TopN
        };
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true && options.ValueKind == JsonValueKind.Object)
            foreach (var property in options.EnumerateObject()) payload[property.Name] = property.Value.Clone();

        using var response = await _client.PostAsync("v1/rerank", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json), cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"CanRouter rerank failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var ranking = root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(item => new RerankingRanking
            {
                Index = item.TryGetProperty("index", out var index) && index.TryGetInt32(out var indexValue) ? indexValue : 0,
                RelevanceScore = item.TryGetProperty("relevance_score", out var score) && score.TryGetSingle(out var scoreValue) ? scoreValue : 0
            }).ToList()
            : [];

        return new RerankingResponse
        {
            Ranking = ranking,
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new() { Timestamp = DateTime.UtcNow, Headers = response.GetHeaders(), ModelId = request.Model.ToModelId(GetIdentifier()), Body = root }
        };
    }
}
