using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ZeroEntropy;

public partial class ZeroEntropyProvider
{
    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request));
        if (request.Documents is null)
            throw new ArgumentException("Documents are required.", nameof(request));
        if (request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));

        var now = DateTime.UtcNow;
        var documents = request.Documents.Values
            .EnumerateArray()
            .Select(d => d.ValueKind == JsonValueKind.String
                ? (d.GetString() ?? string.Empty)
                : d.GetRawText())
            .ToList();

        if (documents.Count == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            ["documents"] = documents,
            ["top_n"] = request.TopN
        };

        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var metadata) == true
            && metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (!payload.ContainsKey(property.Name))
                {
                    payload[property.Name] = property.Value.Clone();
                }
            }
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/models/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, RerankJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            throw new Exception(raw);
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var results = root.TryGetProperty("results", out var resultsEl)
            && resultsEl.ValueKind == JsonValueKind.Array
                ? resultsEl.EnumerateArray()
                    .Select(r => new RerankingRanking
                    {
                        Index = r.GetProperty("index").GetInt32(),
                        RelevanceScore = (float)r.GetProperty("relevance_score").GetDouble()
                    })
                    .ToList()
                : [];

        var providerKey = GetIdentifier();

        return new RerankingResponse
        {
            Ranking = results,
            ProviderMetadata = providerKey.CreatePrimitiveProviderMetadata(new
            {
                total_bytes = TryGetInt64(root, "total_bytes"),
                total_tokens = TryGetInt64(root, "total_tokens"),
                actual_latency_mode = TryGetString(root, "actual_latency_mode"),
                e2e_latency = TryGetDecimal(root, "e2e_latency"),
                inference_latency = TryGetDecimal(root, "inference_latency")
            }),
            Response = new()
            {
                Timestamp = now,
                Id = root.TryGetId(),
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(providerKey),
                Body = root.Clone()
            }
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static long? TryGetInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out var value)
                ? value
                : null;

    private static decimal? TryGetDecimal(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDecimal(out var value)
                ? value
                : null;

    private static readonly JsonSerializerOptions RerankJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
