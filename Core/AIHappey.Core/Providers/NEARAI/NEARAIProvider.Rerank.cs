using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NEARAI;

public partial class NEARAIProvider
{
    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query)) throw new ArgumentException("Query is required.", nameof(request));
        if (request.Documents?.Values.ValueKind != JsonValueKind.Array) throw new ArgumentException("Documents.values must be an array.", nameof(request));

        var documents = request.Documents.Values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new ArgumentException("Documents.values must contain strings.", nameof(request))).ToList();
        if (documents.Count == 0) throw new ArgumentException("At least one document is required.", nameof(request));

        var payload = NEARAIJsonObject(request.ProviderOptions, "model", "query", "documents", "top_n", "return_documents");
        payload["model"] = request.Model;
        payload["query"] = request.Query;
        payload["documents"] = documents;
        if (request.TopN.HasValue) payload["top_n"] = request.TopN.Value;

        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, NEARAIJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"NEARAI rerank request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var rankings = root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(item => new RerankingRanking
            {
                Index = item.TryGetProperty("index", out var index) && index.TryGetInt32(out var indexValue) ? indexValue : 0,
                RelevanceScore = item.TryGetProperty("relevance_score", out var score) && score.TryGetSingle(out var scoreValue) ? scoreValue : 0
            }).ToList()
            : [];

        return new RerankingResponse
        {
            Ranking = rankings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new RerankResponseData
            {
                Id = root.TryGetProperty("id", out var id) ? id.GetString() : null,
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = (root.TryGetProperty("model", out var model) ? model.GetString() : request.Model)?.ToModelId(GetIdentifier())
                    ?? request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private Dictionary<string, object?> NEARAIJsonObject(Dictionary<string, JsonElement>? providerOptions, params string[] reserved)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (providerOptions?.TryGetValue(GetIdentifier(), out var metadata) != true || metadata.ValueKind != JsonValueKind.Object)
            return payload;
        foreach (var property in metadata.EnumerateObject())
            if (!reserved.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && property.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                payload[property.Name] = property.Value.Clone();
        return payload;
    }

    private static readonly JsonSerializerOptions NEARAIJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };


}
