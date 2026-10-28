using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SerenityEdge;

public sealed partial class SerenityEdgeProvider
{
    private static readonly JsonSerializerOptions RerankJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RerankingResponse> RerankingRequest(
        RerankingRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentNullException.ThrowIfNull(request.Documents);
        if (request.Documents.Values.ValueKind != JsonValueKind.Array
            || request.Documents.Values.GetArrayLength() == 0
            || request.Documents.Values.EnumerateArray().Any(document => document.ValueKind != JsonValueKind.String))
            throw new ArgumentException("Serenity Edge reranking requires a non-empty array of string documents.", nameof(request));
        if (request.TopN is < 1)
            throw new ArgumentOutOfRangeException(nameof(request), "topN must be at least 1.");
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && options.ValueKind == JsonValueKind.Object
            && options.EnumerateObject().Any())
            throw new NotSupportedException("Serenity Edge reranking does not document provider-specific options.");

        ApplyAuthHeader();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            ["documents"] = request.Documents.Values,
            ["top_n"] = request.TopN
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, RerankJsonOptions),
                Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, "reranking");

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new JsonException("The response did not contain a results array.");

            var ranking = results.EnumerateArray().Select(result =>
            {
                var index = result.GetProperty("index").GetInt32();
                var score = result.GetProperty("relevance_score").GetSingle();
                if (index < 0 || index >= request.Documents.Values.GetArrayLength()
                    || !float.IsFinite(score) || score is < 0 or > 1)
                    throw new JsonException("Serenity Edge returned an invalid reranking result.");
                return new RerankingRanking { Index = index, RelevanceScore = score };
            }).ToArray();

            return new RerankingResponse
            {
                Ranking = ranking,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
                Response = new RerankResponseData
                {
                    Timestamp = DateTime.UtcNow,
                    Id = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
                    Headers = response.GetHeaders(),
                    ModelId = request.Model.ToModelId(GetIdentifier()),
                    Body = root.Clone()
                }
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Serenity Edge returned an invalid reranking response.", exception);
        }
    }
}
