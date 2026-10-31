using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BergetAI;

public partial class BergetAIProvider
{
    private static readonly JsonSerializerOptions RerankJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        const string url = "v1/rerank";

        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request));
        if (request.Documents is null)
            throw new ArgumentException("Documents are required.", nameof(request));

        if (request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));

        var docs = request.Documents.Values
            .EnumerateArray()
            .Select(d => d.ValueKind == JsonValueKind.String
                ? (d.GetString() ?? string.Empty)
                : throw new ArgumentException("Documents.values must be an array of strings.", nameof(request)))
            .ToList();

        if (docs.Count == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));

        if (request.TopN is <= 0)
            throw new ArgumentException("TopN must be >= 1 when provided.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var metadata = request.GetProviderMetadata<Dictionary<string, JsonElement>>(GetIdentifier());

        var payload = new Dictionary<string, object?>();
        foreach (var (name, value) in metadata ?? [])
        {
            if (name.Equals("return_documents", StringComparison.OrdinalIgnoreCase))
            {
                var returnDocuments = value.Deserialize<bool?>(JsonSerializerOptions.Web);
                if (returnDocuments is not null)
                    payload["return_documents"] = returnDocuments;
                else
                    payload.Remove("return_documents");
            }
            else if (name.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                var user = value.Deserialize<string>(JsonSerializerOptions.Web);
                if (!string.IsNullOrWhiteSpace(user))
                    payload["user"] = user;
                else
                    payload.Remove("user");
            }
            else
            {
                payload[name] = value;
            }
        }

        payload["query"] = request.Query;
        payload["documents"] = docs;
        payload["model"] = request.Model;
        if (request.TopN is not null)
            payload["top_n"] = request.TopN;

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, RerankJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Berget rerank failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
        {
            throw new Exception("Berget rerank response did not contain a 'data' array");
        }

        var ranked = dataEl
            .EnumerateArray()
            .Select(r => new RerankingRanking
            {
                Index = r.GetProperty("index").GetInt32(),
                RelevanceScore = (float)r.GetProperty("relevance_score").GetDouble(),
            })
            .ToList();

        ranked = [.. ranked.OrderByDescending(r => r.RelevanceScore)];

        if (request.TopN is > 0)
            ranked = [.. ranked.Take(request.TopN.Value)];

        var modelId = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
            ? modelEl.GetString() ?? request.Model
            : request.Model;

        var providerMetadata = root.TryGetProperty("usage", out var usageEl) &&
                          usageEl.ValueKind == JsonValueKind.Object ?
                          GetIdentifier().CreatePrimitiveProviderMetadata(new
                          {
                              usage = usageEl.Clone()
                          })
                          : GetIdentifier().CreatePrimitiveProviderMetadata();

        return new RerankingResponse
        {
            Ranking = ranked,
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                Id = root.TryGetId(),
                ModelId = modelId.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }
}
