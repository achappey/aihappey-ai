using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ContextualAI;

public sealed partial class ContextualAIProvider
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
            throw new NotSupportedException("Contextual AI reranking supports only text documents.");
        if (request.Documents.Values.ValueKind != JsonValueKind.Array
            || request.Documents.Values.GetArrayLength() == 0)
            throw new ArgumentException("At least one text document is required.", nameof(request));
        if (request.Documents.Values.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new NotSupportedException("Contextual AI reranking requires string documents.");

        var model = request.Model.StartsWith("contextualai/", StringComparison.OrdinalIgnoreCase)
            ? request.Model["contextualai/".Length..] : request.Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(request));

        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && options.ValueKind == JsonValueKind.Object)
            foreach (var property in options.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        payload["model"] = JsonSerializer.SerializeToElement(model);
        payload["query"] = JsonSerializer.SerializeToElement(request.Query);
        payload["documents"] = request.Documents.Values.Clone();
        if (request.TopN is not null)
            payload["top_n"] = JsonSerializer.SerializeToElement(request.TopN.Value);

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Contextual AI API key.");

        var timestamp = DateTime.UtcNow;
        using var message = new HttpRequestMessage(HttpMethod.Post, "rerank")
        {
            Content = JsonContent.Create(payload)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Contextual AI rerank request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new JsonException("Expected reranking results.");
            var seen = new HashSet<int>();
            var ranking = results.EnumerateArray().Select(item =>
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number
                    || !index.TryGetInt32(out var value) || value < 0
                    || value >= request.Documents.Values.GetArrayLength() || !seen.Add(value))
                    throw new JsonException("Expected a unique input index.");
                if (!item.TryGetProperty("relevance_score", out var score) || score.ValueKind != JsonValueKind.Number
                    || !score.TryGetSingle(out var relevance) || !float.IsFinite(relevance))
                    throw new JsonException("Expected a finite relevance score.");
                return new RerankingRanking { Index = value, RelevanceScore = relevance };
            }).ToArray();
            return new RerankingResponse
            {
                Ranking = ranking,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
                Response = new RerankResponseData
                {
                    Timestamp = timestamp,
                    ModelId = $"{GetIdentifier()}/{model}",
                    Headers = response.GetHeaders(),
                    Body = root.Clone()
                }
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Contextual AI rerank request returned an invalid response.", exception);
        }
    }
}
