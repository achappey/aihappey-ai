using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.BergetAI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BergetAI;

public partial class BergetAIProvider
{
    private static readonly JsonSerializerOptions RerankJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RerankingResponse> RerankingRequestBerget(RerankingRequest request, CancellationToken cancellationToken = default)
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
        var metadata = request.GetProviderMetadata<BergetAIRerankingProviderMetadata>(GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["query"] = request.Query,
            ["documents"] = docs,
            ["model"] = request.Model
        };

        if (request.TopN is not null)
            payload["top_n"] = request.TopN;

        if (metadata?.ReturnDocuments is not null)
            payload["return_documents"] = metadata.ReturnDocuments;

        if (!string.IsNullOrWhiteSpace(metadata?.User))
            payload["user"] = metadata.User;

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
            warnings.Add(new
            {
                type = "provider_response_missing_field",
                feature = "data",
                details = "Berget rerank response did not contain a 'data' array."
            });

            return new RerankingResponse
            {
                Ranking = [],
                Warnings = warnings,
                Response = new ResponseData
                {
                    Timestamp = now,
                    ModelId = request.Model,
                    Body = raw
                }
            };
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

        return new RerankingResponse
        {
            Ranking = ranked,
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = modelId,
                Body = raw
            }
        };
    }
}

