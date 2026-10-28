using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AnyRouter;

public partial class AnyRouterProvider
{
    private static readonly JsonSerializerOptions AnyRouterRerankJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<RerankingResponse> RerankingRequestInternal(
        RerankingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        if (request.Documents is null || request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array of strings.", nameof(request));
        if (request.TopN is <= 0)
            throw new ArgumentException("TopN must be at least one when provided.", nameof(request));

        var documents = request.Documents.Values.EnumerateArray()
            .Select(value => value.ValueKind switch
            {
                JsonValueKind.String => value.Clone(),
                JsonValueKind.Object when value.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String
                    && value.EnumerateObject().Count() == 1 => value.Clone(),
                _ => throw new ArgumentException(
                    "Documents.values must contain strings or objects with only a string 'text' field.",
                    nameof(request))
            })
            .ToList();
        if (documents.Count == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));
        if (documents.Count > 1000)
            throw new ArgumentException("AnyRouter rerank supports at most 1,000 documents.", nameof(request));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = GetAnyRouterNativeModel(request.Model),
            ["query"] = request.Query,
            ["documents"] = documents
        };
        if (request.TopN is not null)
            payload["top_n"] = request.TopN.Value;

        MergeAnyRouterRerankOptions(payload, request);
        var warnings = new List<object>();
        if (!string.Equals(request.Documents.Type, "text", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "unsupported", feature = "documents.type", details = "AnyRouter reranking accepts text documents." });

        ApplyAuthHeader();
        var timestamp = DateTime.UtcNow;
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, AnyRouterRerankJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(raw)
                    ? $"AnyRouter rerank request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"AnyRouter rerank request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement.Clone();
            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new JsonException("AnyRouter rerank response requires a results array.");
            var ranking = new List<RerankingRanking>();
            var seenIndexes = new HashSet<int>();
            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object
                    || !result.TryGetProperty("index", out var indexElement)
                    || !indexElement.TryGetInt32(out var index)
                    || index < 0 || index >= documents.Count || !seenIndexes.Add(index)
                    || !result.TryGetProperty("relevance_score", out var scoreElement)
                    || !scoreElement.TryGetSingle(out var score))
                    throw new JsonException("AnyRouter rerank result contains an invalid document index or score.");
                if (!float.IsFinite(score) || score is < 0 or > 1)
                    throw new JsonException("AnyRouter relevance scores must be finite values in [0,1].");
                ranking.Add(new RerankingRanking { Index = index, RelevanceScore = score });
            }

            if (request.TopN is > 0)
                ranking = [.. ranking.OrderByDescending(item => item.RelevanceScore).Take(request.TopN.Value)];
            else
                ranking = [.. ranking.OrderByDescending(item => item.RelevanceScore)];

            return new RerankingResponse
            {
                Ranking = ranking,
                Warnings = warnings,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(
                    root.TryGetProperty("usage", out var usage) ? new { usage = usage.Clone() } : new { }),
                Response = new()
                {
                    Timestamp = timestamp,
                    Headers = response.GetHeaders(),
                    Id = ReadAnyRouterString(root, "id"),
                    ModelId = (ReadAnyRouterString(root, "model") ?? GetAnyRouterNativeModel(request.Model))
                        .ToModelId(GetIdentifier()),
                    Body = root
                }
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new InvalidOperationException("AnyRouter returned an invalid rerank response.", exception);
        }
    }

    private static void MergeAnyRouterRerankOptions(
        IDictionary<string, object?> payload, RerankingRequest request)
    {
        if (request.ProviderOptions?.TryGetValue("anyrouter", out var options) != true)
            return;
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.anyrouter must be an object.", nameof(request));

        foreach (var property in options.EnumerateObject())
        {
            switch (property.Name)
            {
                case "return_documents" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    payload[property.Name] = property.Value.GetBoolean();
                    break;
                case "max_tokens_per_doc" when property.Value.TryGetInt32(out var maxTokens) && maxTokens >= 1:
                    payload[property.Name] = maxTokens;
                    break;
                case "session_id" when property.Value.ValueKind == JsonValueKind.String:
                    payload[property.Name] = property.Value.GetString();
                    break;
                case "return_documents":
                case "max_tokens_per_doc":
                case "session_id":
                    throw new ArgumentException($"providerOptions.anyrouter.{property.Name} has an invalid value.", nameof(request));
                default:
                    throw new NotSupportedException($"AnyRouter rerank does not document option '{property.Name}'.");
            }
        }
    }
}
