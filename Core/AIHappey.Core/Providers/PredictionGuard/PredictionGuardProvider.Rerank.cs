using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PredictionGuard;

public sealed partial class PredictionGuardProvider
{
    public async Task<RerankingResponse> RerankingRequest(
        RerankingRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Model and query are required.", nameof(request));
        if (request.TopN is <= 0)
            throw new ArgumentException("TopN must be positive.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Documents);
        if (request.Documents.Type != "text")
            throw new NotSupportedException("Prediction Guard reranking supports text documents.");
        var documents = request.Documents.Values;
        if (documents.ValueKind != JsonValueKind.Array || documents.GetArrayLength() == 0 ||
            documents.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString())))
            throw new ArgumentException("At least one non-empty text document is required.", nameof(request));

        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Prediction Guard provider options must be an object.", nameof(request));
            foreach (var property in options.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }
        var model = NativeModel(request.Model);
        payload["model"] = JsonSerializer.SerializeToElement(model);
        payload["query"] = JsonSerializer.SerializeToElement(request.Query);
        payload["documents"] = documents.Clone();
        var timestamp = DateTime.UtcNow;
        var (root, headers) = await SendJsonAsync("rerank", payload, cancellationToken);
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected Prediction Guard reranking results.");
        var seen = new HashSet<int>();
        var ranking = results.EnumerateArray().Select(item => new RerankingRanking
        {
            Index = ReadIndex(item, documents.GetArrayLength(), seen),
            RelevanceScore = item.TryGetProperty("relevance_score", out var score)
                ? ReadFloat(score) : throw new JsonException("Expected a relevance score.")
        }).OrderByDescending(item => item.RelevanceScore).ToArray();
        return new RerankingResponse
        {
            Ranking = request.TopN is { } topN ? ranking.Take(topN).ToArray() : ranking,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = $"{GetIdentifier()}/{model}",
                Headers = headers,
                Body = root
            }
        };
    }
}
