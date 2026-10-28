using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vultr;

public partial class VultrProvider
{
    public async Task<RerankingResponse> RerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentNullException.ThrowIfNull(request.Documents);

        if (!string.Equals(request.Documents.Type, "text", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Vultr reranking supports text documents only.");
        if (request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Vultr reranking documents must be an array of strings.", nameof(request));

        var documents = request.Documents.Values.EnumerateArray().ToArray();
        if (documents.Length == 0
            || documents.Any(document => document.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(document.GetString())))
        {
            throw new ArgumentException("Vultr reranking requires at least one non-empty text document.", nameof(request));
        }

        if (request.TopN is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "topN must be a positive integer.");

        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var providerOptions) == true)
        {
            if (providerOptions.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.vultr must be an object.", nameof(request));
            if (providerOptions.EnumerateObject().Any())
                throw new NotSupportedException("Vultr reranking does not document provider-specific options.");
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            ["documents"] = documents.Select(document => document.GetString()).ToArray(),
            ["top_n"] = request.TopN
        };

        ApplyAuthHeader();
        using var response = await _client.PostAsync(
            "v1/rerank",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(raw)
                    ? $"Vultr reranking failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"Vultr reranking failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("results", out var resultItems)
                || resultItems.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Vultr rerank response must contain a results array.");
            }

            var ranking = resultItems.EnumerateArray().Select(item =>
            {
                if (!item.TryGetProperty("index", out var indexElement)
                    || !indexElement.TryGetInt32(out var index)
                    || index < 0 || index >= documents.Length)
                {
                    throw new JsonException("Vultr rerank response contains an invalid document index.");
                }

                var scoreProperty = item.TryGetProperty("score", out var score)
                    ? score
                    : item.TryGetProperty("relevance_score", out var relevanceScore)
                        ? relevanceScore
                        : default;
                if (scoreProperty.ValueKind != JsonValueKind.Number
                    || !scoreProperty.TryGetSingle(out var relevance)
                    || !float.IsFinite(relevance))
                {
                    throw new JsonException("Vultr rerank response contains an invalid score.");
                }

                return new RerankingRanking { Index = index, RelevanceScore = relevance };
            }).ToArray();

            return new RerankingResponse
            {
                Ranking = ranking,
                Warnings = [],
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
                Response = new RerankResponseData
                {
                    Timestamp = DateTime.UtcNow,
                    ModelId = request.Model.ToModelId(GetIdentifier()),
                    Headers = response.GetHeaders(),
                    Body = root.Clone(),
                    Id = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString()
                        : null
                }
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Vultr returned an invalid reranking response.", exception);
        }
    }
}
