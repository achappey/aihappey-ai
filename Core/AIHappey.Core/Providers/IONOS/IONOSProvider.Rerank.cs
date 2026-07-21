using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.IONOS;

public partial class IONOSProvider
{
    private static readonly JsonSerializerOptions IonosRerankJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request));
        if (request.Documents is null || request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));
        if (request.Documents.Values.GetArrayLength() == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));
        if (request.TopN is < 0)
            throw new ArgumentException("TopN must be greater than or equal to zero.", nameof(request));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            // Keep strings and multimodal document objects intact for IONOS-compatible rerank models.
            ["documents"] = request.Documents.Values
        };

        if (request.TopN is not null)
            payload["top_n"] = request.TopN;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, IonosRerankJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"IONOS rerank failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("IONOS rerank response did not contain a 'results' array.");

        var ranking = results.EnumerateArray().Select(result => new RerankingRanking
        {
            Index = result.GetProperty("index").GetInt32(),
            RelevanceScore = (float)result.GetProperty("relevance_score").GetDouble()
        }).OrderByDescending(result => result.RelevanceScore).ToList();

        if (request.TopN is > 0)
            ranking = [.. ranking.Take(request.TopN.Value)];

        return new RerankingResponse
        {
            Ranking = ranking,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Id = root.TryGetId(),
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = raw
            }
        };
    }

}
