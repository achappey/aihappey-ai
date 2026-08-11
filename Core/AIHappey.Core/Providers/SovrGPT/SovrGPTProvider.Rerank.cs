using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.SovrGPT;

public partial class SovrGPTProvider
{
    private static readonly JsonSerializerOptions RerankingJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RerankingResponse> RerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request));
        if (request.Documents is null)
            throw new ArgumentException("Documents are required.", nameof(request));
        if (request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));
        if (request.TopN is <= 0)
            throw new ArgumentException("TopN must be >= 1 when provided.", nameof(request));

        ApplyAuthHeader();
        var now = DateTime.UtcNow;
        var payload = BuildRerankingPayload(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, RerankingJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"SovrGPT rerank request failed ({(int)response.StatusCode})."
                : $"SovrGPT rerank request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var ranking = root.TryGetProperty("results", out var results)
            && results.ValueKind == JsonValueKind.Array
                ? results.EnumerateArray()
                    .Select(result => new RerankingRanking
                    {
                        Index = result.GetProperty("index").GetInt32(),
                        RelevanceScore = (float)result.GetProperty("relevance_score").GetDouble()
                    })
                    .ToList()
                : [];

        return new RerankingResponse
        {
            Ranking = ranking,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = now,
                Id = root.TryGetId(),
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private Dictionary<string, object?> BuildRerankingPayload(RerankingRequest request)
    {
        var payload = new Dictionary<string, object?>();
        var options = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        if (options.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in options.EnumerateObject())
            {
                if (IsCanonicalRerankingProperty(property.Name))
                    continue;

                payload[property.Name] = property.Value.Clone();
            }
        }

        payload["model"] = request.Model;
        payload["query"] = request.Query;
        payload["documents"] = request.Documents.Values;

        if (request.TopN is not null)
            payload["top_n"] = request.TopN;

        return payload;
    }

    private static bool IsCanonicalRerankingProperty(string propertyName)
        => propertyName.Equals("model", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("query", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("documents", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("top_n", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("topN", StringComparison.OrdinalIgnoreCase);
}
