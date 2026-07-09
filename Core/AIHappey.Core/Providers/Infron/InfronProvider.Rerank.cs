using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Infron;

public partial class InfronProvider
{
    private static readonly JsonSerializerOptions InfronRerankJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<RerankingResponse> InfronRerankingRequest(
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
                details = "Infron rerank expects text documents. Documents.values is forwarded as strings."
            });
        }

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildInfronRerankPayload(request, metadata);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, InfronRerankJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Infron rerank request failed ({(int)response.StatusCode})."
                : $"Infron rerank request failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        var ranked = root.TryGetProperty("results", out var resultsEl)
            && resultsEl.ValueKind == JsonValueKind.Array
                ? resultsEl.EnumerateArray()
                    .Select(ReadInfronRerankRanking)
                    .OrderByDescending(r => r.RelevanceScore)
                    .ThenBy(r => r.Index)
                    .ToList()
                : [];

        if (ranked.Count == 0 && (!root.TryGetProperty("results", out _) || resultsEl.ValueKind != JsonValueKind.Array))
        {
            warnings.Add(new
            {
                type = "provider_response_missing_field",
                feature = "results",
                details = "Infron rerank response did not contain a 'results' array."
            });
        }

        if (request.TopN is > 0)
            ranked = [.. ranked.Take(request.TopN.Value)];

        return new RerankingResponse
        {
            Ranking = ranked,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = ResolveInfronTimestamp(root, now),
                ModelId = root.TryGetString("model")?.ToModelId(GetIdentifier())
                    ?? request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    internal static Dictionary<string, object?> BuildInfronRerankPayload(
        RerankingRequest request,
        JsonElement? metadata = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            ["documents"] = ReadInfronRerankDocuments(request)
        };

        if (request.TopN is not null)
            payload["top_n"] = request.TopN;

        MergeInfronRerankProviderOptions(payload, metadata);

        return payload;
    }

    private static void MergeInfronRerankProviderOptions(Dictionary<string, object?> payload, JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } options)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (string.Equals(property.Name, "model", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "query", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "documents", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "top_n", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "topN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            payload[property.Name] = property.Value.Clone();
        }
    }

    private static IReadOnlyList<string> ReadInfronRerankDocuments(RerankingRequest request)
    {
        if (request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));

        var documents = request.Documents.Values
            .EnumerateArray()
            .Select(static item => item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? string.Empty
                : throw new ArgumentException("Documents.values must be an array of strings.", nameof(request)))
            .ToList();

        if (documents.Count == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));

        return documents;
    }

    private static RerankingRanking ReadInfronRerankRanking(JsonElement result)
        => new()
        {
            Index = result.TryGetProperty("index", out var indexEl)
                && indexEl.ValueKind == JsonValueKind.Number
                    ? indexEl.GetInt32()
                    : 0,
            RelevanceScore = result.TryGetProperty("relevance_score", out var scoreEl)
                && scoreEl.ValueKind == JsonValueKind.Number
                    ? (float)scoreEl.GetDouble()
                    : result.TryGetProperty("relevanceScore", out var altScoreEl)
                        && altScoreEl.ValueKind == JsonValueKind.Number
                            ? (float)altScoreEl.GetDouble()
                            : 0f
        };
}
