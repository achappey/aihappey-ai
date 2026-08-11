using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MoleAPI;

public partial class MoleAPIProvider
{
    private static readonly JsonSerializerOptions RerankingJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query)) throw new ArgumentException("Query is required.", nameof(request));

        ApplyAuthHeader();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = NormalizeProviderModelId(request.Model),
            ["query"] = request.Query,
            ["documents"] = request.Documents.Values,
            ["top_n"] = request.TopN
        };
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true && options.ValueKind == JsonValueKind.Object)
            foreach (var property in options.EnumerateObject()) payload[property.Name] = property.Value.Clone();

        using var response = await _client.PostAsync("v1/rerank",
            new StringContent(JsonSerializer.Serialize(payload, RerankingJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json), cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"MoleAPI reranking request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var ranking = root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(item => new RerankingRanking
            {
                Index = item.TryGetProperty("index", out var index) ? index.GetInt32() : 0,
                RelevanceScore = item.TryGetProperty("relevance_score", out var score) ? score.GetSingle() : 0
            }).ToList()
            : [];

        return new RerankingResponse
        {
            Ranking = ranking,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new()
            {
                Id = root.TryGetProperty("id", out var id) ? id.GetString() : null,
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

}
