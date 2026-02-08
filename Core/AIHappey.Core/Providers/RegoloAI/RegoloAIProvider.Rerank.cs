using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RegoloAI;

public partial class RegoloAIProvider
{
    private const string DefaultRerankInstruction = "Given a query, retrieve relevant passages that answer the query";

    private static readonly JsonSerializerOptions RerankJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RerankingResponse> RerankingRequestRegolo(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request));
        if (request.Documents is null)
            throw new ArgumentException("Documents are required.", nameof(request));

        if (request.TopN is <= 0)
            throw new ArgumentException("TopN must be >= 1 when provided.", nameof(request));

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

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = EnsureTaggedQuery(request.Query),
            ["documents"] = docs.Select(EnsureTaggedDocument).ToList(),
            ["top_n"] = request.TopN
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, RerankJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Regolo rerank failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
        {
            warnings.Add(new
            {
                type = "provider_response_missing_field",
                feature = "results",
                details = "Regolo rerank response did not contain a 'results' array."
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

        var rankings = new List<RerankingRanking>();

        foreach (var item in resultsEl.EnumerateArray())
        {
            if (!item.TryGetProperty("index", out var indexEl) || indexEl.ValueKind != JsonValueKind.Number)
                continue;

            if (!item.TryGetProperty("relevance_score", out var scoreEl) || scoreEl.ValueKind != JsonValueKind.Number)
                continue;

            rankings.Add(new RerankingRanking
            {
                Index = indexEl.GetInt32(),
                RelevanceScore = (float)scoreEl.GetDouble()
            });
        }

        rankings = [.. rankings.OrderByDescending(x => x.RelevanceScore)];

        if (request.TopN is > 0)
            rankings = [.. rankings.Take(request.TopN.Value)];

        return new RerankingResponse
        {
            Ranking = rankings,
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = raw
            }
        };
    }

    private static string EnsureTaggedQuery(string query)
    {
        var trimmed = query.Trim();

        if (ContainsTag(trimmed, "<Instruct>:") && ContainsTag(trimmed, "<Query>:"))
            return trimmed;

        var queryText = trimmed;
        if (queryText.StartsWith("<Query>:", StringComparison.OrdinalIgnoreCase))
            queryText = queryText["<Query>:".Length..].Trim();

        return $"<Instruct>: {DefaultRerankInstruction}\n<Query>: {queryText}";
    }

    private static string EnsureTaggedDocument(string document)
    {
        var trimmed = document.Trim();

        if (ContainsTag(trimmed, "<Document>:"))
            return trimmed;

        return $"<Document>: {trimmed}";
    }

    private static bool ContainsTag(string input, string tag)
        => input.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0;
}
