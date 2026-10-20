using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Isaacus;

public partial class IsaacusProvider
{
    public async Task<RerankingResponse> RerankingRequest(
        RerankingRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Model and query are required.", nameof(request));
        if (request.TopN is <= 0)
            throw new ArgumentException("TopN must be a positive integer.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.Documents);
        if (request.Documents.Type != "text")
            throw new NotSupportedException("Isaacus reranking supports only text documents.");
        if (request.Documents.Values.ValueKind != JsonValueKind.Array
            || request.Documents.Values.GetArrayLength() == 0)
            throw new ArgumentException("At least one text document is required.", nameof(request));
        var texts = request.Documents.Values.EnumerateArray().ToArray();
        if (texts.Any(item => item.ValueKind != JsonValueKind.String))
            throw new NotSupportedException("Isaacus reranking requires string documents.");
        if (texts.Any(item => string.IsNullOrWhiteSpace(item.GetString())))
            throw new ArgumentException("Text documents cannot be empty.", nameof(request));

        var model = NativeModel(request.Model);
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && options.ValueKind == JsonValueKind.Object)
            foreach (var property in options.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        payload["model"] = JsonSerializer.SerializeToElement(model);
        payload["query"] = JsonSerializer.SerializeToElement(request.Query);
        payload["texts"] = request.Documents.Values.Clone();
        if (request.TopN is not null)
            payload["top_n"] = JsonSerializer.SerializeToElement(request.TopN.Value);

        var timestamp = DateTime.UtcNow;
        var native = await SendAsync("rerankings", payload, cancellationToken);
        try
        {
            var root = native.Root;
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new JsonException("Expected reranking results.");
            _ = InputTokens(root);
            var seen = new HashSet<int>();
            var ranking = results.EnumerateArray().Select(item =>
            {
                var index = ReadIndex(item, texts.Length, seen);
                if (!item.TryGetProperty("score", out var score))
                    throw new JsonException("Expected a relevance score.");
                return new RerankingRanking { Index = index, RelevanceScore = ReadFloat(score) };
            }).ToArray();
            return new RerankingResponse
            {
                Ranking = ranking,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
                Response = new RerankResponseData
                {
                    Timestamp = timestamp,
                    ModelId = $"{GetIdentifier()}/{model}",
                    Headers = native.Headers,
                    Body = root
                }
            };
        }
        catch (JsonException exception)
        {
            throw InvalidResponse("rerankings", exception);
        }
    }
}
