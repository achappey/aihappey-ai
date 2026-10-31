using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.GreenPT;

public partial class GreenPTProvider
{
    private static readonly JsonSerializerOptions RerankJsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<RerankingResponse> RerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request));
        if (request.Documents is null)
            throw new ArgumentException("Documents are required.", nameof(request));
        if (request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));

        var now = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var payload = new Dictionary<string, object?>();
        if (metadata.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            throw new JsonException("GreenPT provider options must be a JSON object.");

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (property.Name == "return_documents")
                {
                    var returnDocuments = property.Value.Deserialize<bool?>();
                    if (returnDocuments is not null)
                        payload[property.Name] = returnDocuments;

                    continue;
                }

                payload[property.Name] = property.Value.Clone();
            }
        }

        payload["model"] = request.Model;
        payload["query"] = request.Query;
        payload["documents"] = request.Documents.Values;
        payload["top_n"] = request.TopN;

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, RerankJsonOpts),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception(raw);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var ranking = root.TryGetProperty("results", out var resultsEl)
            && resultsEl.ValueKind == JsonValueKind.Array
                ? resultsEl.EnumerateArray()
                    .Select(r => new RerankingRanking
                    {
                        Index = r.GetProperty("index").GetInt32(),
                        RelevanceScore = (float)r.GetProperty("relevance_score").GetDouble(),
                    })
                    .ToList()
                : [];    

        var usage = root.TryGetProperty("usage", out var usageEl)
            && usageEl.ValueKind == JsonValueKind.Object
                ? usageEl.Clone()
                : (JsonElement?)null;

        return new RerankingResponse
        {
            Ranking = ranking,
            ProviderMetadata = GetIdentifier()
                .CreatePrimitiveProviderMetadata(usage != null ? new
                {
                    usage
                } : null),
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
