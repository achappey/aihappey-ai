using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Fireworks;
using AIHappey.Core.AI;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Fireworks;

public partial class FireworksProvider : IModelProvider
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
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request));
        if (request.Documents is null)
            throw new ArgumentException("Documents are required.", nameof(request));

        // Fireworks expects: documents: string[]
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

        // Fireworks schema: top_n must be >= 1 if provided.
        if (request.TopN is <= 0)
            throw new ArgumentException("TopN must be >= 1 when provided.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var metadata = request.GetProviderMetadata<FireworksRerankingProviderMetadata>(GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            ["documents"] = docs
        };

        if (!string.IsNullOrWhiteSpace(metadata?.Task))
            payload["task"] = metadata.Task;

        if (metadata?.ReturnDocuments is not null)
            payload["return_documents"] = metadata.ReturnDocuments;

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
            throw new InvalidOperationException($"Fireworks rerank failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
        {
            warnings.Add(new
            {
                type = "provider_response_missing_field",
                feature = "data",
                details = "Fireworks rerank response did not contain a 'data' array."
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

        // Fireworks orders by highest first already, but we don't rely on that.
        ranked = [.. ranked.OrderByDescending(r => r.RelevanceScore)];

        // Keep parity with other providers: request.TopN also trims client-side.
        if (request.TopN is > 0)
            ranked = [.. ranked.Take(request.TopN.Value)];

        return new RerankingResponse
        {
            Ranking = ranked,
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = raw
            }
        };
    }
}

