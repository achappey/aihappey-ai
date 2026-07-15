using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ILMU;

public partial class ILMUProvider
{
    private async Task<RerankingResponse> ILMURerankingRequest(
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
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildILMURerankPayload(request, metadata, warnings);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ILMUJsonOptions),
                Encoding.UTF8,
                MediaTypeHeaderValue.Parse(MediaTypeNames.Application.Json))
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"ILMU rerank request failed ({(int)response.StatusCode})."
                : $"ILMU rerank request failed ({(int)response.StatusCode}): {raw}");

        if (!ILMUTryParseJson(raw, out var document))
            throw new InvalidOperationException("ILMU rerank response was not valid JSON.");

        using (document)
        {
            var root = document.RootElement.Clone();
            var ranked = root.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array
                ? resultsElement.EnumerateArray()
                    .Select(ReadILMURerankRanking)
                    .OrderByDescending(ranking => ranking.RelevanceScore)
                    .ToList()
                : [];

            if (!root.TryGetProperty("results", out _) || resultsElement.ValueKind != JsonValueKind.Array)
                warnings.Add(new { type = "provider_response_missing_field", feature = "results", details = "ILMU rerank response did not contain a results array." });

            if (request.TopN is > 0)
                ranked = [.. ranked.Take(request.TopN.Value)];

            return new RerankingResponse
            {
                Ranking = ranked,
                Warnings = warnings,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
                Response = new RerankResponseData
                {
                    Timestamp = now,
                    Headers = response.GetHeaders(),
                    Id = root.TryGetId(),
                    ModelId = request.Model.ToModelId(GetIdentifier()),
                    Body = root
                }
            };
        }
    }

    private static Dictionary<string, object?> BuildILMURerankPayload(
        RerankingRequest request,
        JsonElement metadata,
        List<object> warnings)
    {
        if (!string.Equals(request.Documents.Type, "text", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "unsupported", feature = "documents.type", details = "ILMU rerank expects text document strings. Documents.values was forwarded as strings." });

        var payload = ILMUJsonObjectToDictionary(metadata);

        payload["model"] = request.Model.Trim();
        payload["query"] = request.Query;
        payload["documents"] = ReadILMURerankDocuments(request);

        if (request.TopN is > 0)
            payload["top_n"] = request.TopN.Value;

        return payload;
    }

    private static IReadOnlyList<string> ReadILMURerankDocuments(RerankingRequest request)
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

    private static RerankingRanking ReadILMURerankRanking(JsonElement result)
        => new()
        {
            Index = ILMUTryGetInt(result, "index") ?? 0,
            RelevanceScore = ILMUTryGetFloat(result, "relevance_score", "relevanceScore", "score") ?? 0f
        };
}
