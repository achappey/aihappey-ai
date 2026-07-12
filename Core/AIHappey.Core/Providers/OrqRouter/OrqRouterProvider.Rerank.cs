using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OrqRouter;

public partial class OrqRouterProvider
{
    private async Task<RerankingResponse> OrqRouterRerankingRequest(
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
        if (request.TopN is <= 0)
            throw new ArgumentException("TopN must be >= 1 when provided.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (!string.Equals(request.Documents.Type, "text", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "documents.type",
                details = "OrqRouter rerank expects text documents. Documents.values is forwarded as strings."
            });
        }

        var payload = BuildOrqRouterRerankPayload(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v2/router/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OrqRouterJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OrqRouter rerank request failed ({(int)response.StatusCode})."
                : $"OrqRouter rerank request failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var ranking = ExtractOrqRouterReranking(root, warnings);

        if (request.TopN is > 0)
            ranking = [.. ranking.Take(request.TopN.Value)];

        return new RerankingResponse
        {
            Ranking = ranking,
            Warnings = warnings,
            ProviderMetadata = BuildOrqRouterProviderMetadata(root),
            Response = new()
            {
                Timestamp = now,
                Id = root.TryGetId(),
                ModelId = ReadOrqRouterString(root, "model")?.ToModelId(GetIdentifier())
                    ?? request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private static Dictionary<string, object?> BuildOrqRouterRerankPayload(RerankingRequest request)
    {
        var providerOptions = ReadOrqRouterProviderOptions(request.ProviderOptions);
        var payload = new Dictionary<string, object?>
        {
            ["query"] = request.Query,
            ["documents"] = ReadOrqRouterRerankDocuments(request),
            ["model"] = request.Model
        };

        if (request.TopN is not null)
            payload["top_n"] = request.TopN.Value;

        MergeOrqRouterProviderOptions(payload, providerOptions, ReservedOrqRouterRerankKeys);

        return payload;
    }

    private static readonly HashSet<string> ReservedOrqRouterRerankKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "query", "documents", "model", "top_n"
    };

    private static IReadOnlyList<string> ReadOrqRouterRerankDocuments(RerankingRequest request)
    {
        if (request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));

        var documents = request.Documents.Values
            .EnumerateArray()
            .Select(document => document.ValueKind == JsonValueKind.String
                ? document.GetString() ?? string.Empty
                : throw new ArgumentException("Documents.values must be an array of strings.", nameof(request)))
            .ToList();

        if (documents.Count == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));

        return documents;
    }

    private static List<RerankingRanking> ExtractOrqRouterReranking(JsonElement root, List<object> warnings)
    {
        if (!root.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
        {
            warnings.Add(new
            {
                type = "provider_response_missing_field",
                feature = "results",
                details = "OrqRouter rerank response did not contain a 'results' array."
            });

            return [];
        }

        return resultsEl
            .EnumerateArray()
            .Where(result => result.ValueKind == JsonValueKind.Object)
            .Select(result => new RerankingRanking
            {
                Index = ReadOrqRouterInt(result, "index") ?? 0,
                RelevanceScore = ReadOrqRouterFloat(result, "relevance_score") ?? 0f
            })
            .OrderByDescending(result => result.RelevanceScore)
            .ToList();
    }
}
