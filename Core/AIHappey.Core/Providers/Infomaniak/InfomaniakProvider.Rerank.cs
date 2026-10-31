using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Infomaniak;

public partial class InfomaniakProvider
{
    private static readonly JsonSerializerOptions RerankJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request));

        var now = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var payload = new Dictionary<string, object?>();
        if (metadata.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            throw new JsonException("Infomaniak provider options must be a JSON object.");

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (property.Name is "max_tokens_per_doc" or "priority")
                {
                    var value = property.Value.Deserialize<int?>();
                    if (value is not null)
                        payload[property.Name] = value;

                    continue;
                }

                payload[property.Name] = property.Value.Clone();
            }
        }

        payload["model"] = request.Model;
        payload["query"] = request.Query;
        payload["documents"] = request.Documents.Values;
        payload["top_n"] = request.TopN;

        var productId = await GetProductIdAsync(cancellationToken);
        var reqJson = JsonSerializer.Serialize(payload, RerankJson);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"2/ai/{productId}/cohere/v2/rerank")
        {
            Content = new StringContent(reqJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Infomaniak rerank failed: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var results = root.TryGetProperty("results", out var resultsEl)
            && resultsEl.ValueKind == JsonValueKind.Array
                ? resultsEl.EnumerateArray()
                    .Select(r => new RerankingRanking
                    {
                        Index = r.GetProperty("index").GetInt32(),
                        RelevanceScore = (float)r.GetProperty("relevance_score").GetDouble()
                    })
                    .ToList()
                : [];

        return new RerankingResponse
        {
            Ranking = results,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
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
}
