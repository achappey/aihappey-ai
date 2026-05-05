using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.OpenRouter;

public partial class OpenRouterProvider
{
    private static readonly JsonSerializerOptions OpenRouterRerankJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<RerankingResponse> RerankingRequestOpenRouter(
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
                details = "OpenRouter rerank expects text documents. Documents.values is forwarded as strings."
            });
        }

        var payload = BuildOpenRouterRerankPayload(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OpenRouterRerankJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OpenRouter rerank request failed ({(int)resp.StatusCode})."
                : $"OpenRouter rerank request failed ({(int)resp.StatusCode}): {raw}");
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        var ranked = root.TryGetProperty("results", out var resultsEl)
            && resultsEl.ValueKind == JsonValueKind.Array
                ? resultsEl.EnumerateArray()
                    .Select(ReadOpenRouterRerankRanking)
                    .OrderByDescending(r => r.RelevanceScore)
                    .ToList()
                : [];

        if (ranked.Count == 0 && (!root.TryGetProperty("results", out _) || resultsEl.ValueKind != JsonValueKind.Array))
        {
            warnings.Add(new
            {
                type = "provider_response_missing_field",
                feature = "results",
                details = "OpenRouter rerank response did not contain a 'results' array."
            });
        }

        if (request.TopN is > 0)
            ranked = [.. ranked.Take(request.TopN.Value)];

        return new RerankingResponse
        {
            Ranking = ranked,
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = ReadOpenRouterRerankString(root, "model") ?? request.Model,
                Body = new
                {
                    request = payload,
                    response = root,
                    statusCode = (int)resp.StatusCode,
                    id = ReadOpenRouterRerankString(root, "id"),
                    provider = ReadOpenRouterRerankString(root, "provider"),
                    usage = root.TryGetProperty("usage", out var usageEl)
                        && usageEl.ValueKind == JsonValueKind.Object
                            ? usageEl.Clone()
                            : (JsonElement?)null
                }
            }
        };
    }

    private static Dictionary<string, object?> BuildOpenRouterRerankPayload(RerankingRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["documents"] = ReadOpenRouterRerankDocuments(request),
            ["model"] = request.Model,
            ["query"] = request.Query
        };

        if (request.TopN is not null)
            payload["top_n"] = request.TopN;

        MergeOpenRouterRerankProviderOptions(payload, request);

        return payload;
    }

    private static void MergeOpenRouterRerankProviderOptions(Dictionary<string, object?> payload, RerankingRequest request)
    {
        if (request.ProviderOptions is null)
            return;

        if (!request.ProviderOptions.TryGetValue("openrouter", out var providerOptions))
            return;

        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private static IReadOnlyList<string> ReadOpenRouterRerankDocuments(RerankingRequest request)
    {
        if (request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));

        var documents = request.Documents.Values
            .EnumerateArray()
            .Select(d => d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? string.Empty
                : throw new ArgumentException("Documents.values must be an array of strings.", nameof(request)))
            .ToList();

        if (documents.Count == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));

        return documents;
    }

    private static RerankingRanking ReadOpenRouterRerankRanking(JsonElement result)
        => new()
        {
            Index = result.TryGetProperty("index", out var indexEl)
                && indexEl.ValueKind == JsonValueKind.Number
                    ? indexEl.GetInt32()
                    : 0,
            RelevanceScore = result.TryGetProperty("relevance_score", out var scoreEl)
                && scoreEl.ValueKind == JsonValueKind.Number
                    ? (float)scoreEl.GetDouble()
                    : 0f
        };

    private static string? ReadOpenRouterRerankString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}
