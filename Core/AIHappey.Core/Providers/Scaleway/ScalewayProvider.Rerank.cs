using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Scaleway;

public partial class ScalewayProvider
{
    private static readonly JsonSerializerOptions RerankJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class ScalewayRerankResultDocument
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private sealed class ScalewayRerankResult
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("relevance_score")]
        public double RelevanceScore { get; set; }

        [JsonPropertyName("document")]
        public ScalewayRerankResultDocument? Document { get; set; }
    }

    public async Task<RerankingResponse> RerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
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

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            ["documents"] = docs
        };

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
            throw new InvalidOperationException($"Scaleway rerank failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
        {
            warnings.Add(new
            {
                type = "provider_response_missing_field",
                feature = "results",
                details = "Scaleway rerank response did not contain a 'results' array."
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

        var ranked = resultsEl
            .Deserialize<List<ScalewayRerankResult>>(RerankJson)?
            .Select(r => new RerankingRanking
            {
                Index = r.Index,
                RelevanceScore = (float)r.RelevanceScore
            })
            .OrderByDescending(r => r.RelevanceScore)
            .ToList() ?? [];

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

