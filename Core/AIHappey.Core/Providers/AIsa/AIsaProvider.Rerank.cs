using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIsa;

public partial class AIsaProvider
{
    private static readonly JsonSerializerOptions AIsaRerankJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        if (request.Documents is null || request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array of strings.", nameof(request));
        if (request.TopN is <= 0)
            throw new ArgumentException("TopN must be at least one when provided.", nameof(request));

        var documents = request.Documents.Values.EnumerateArray().Select(value =>
            value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : throw new ArgumentException("Documents.values must contain only strings.", nameof(request))).ToList();
        if (documents.Count == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));

        List<object> warnings = [];
        if (!string.Equals(request.Documents.Type, "text", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "unsupported", feature = "documents.type", details = "AIsa rerank accepts text documents." });

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = AIsaJsonObjectToDictionary(metadata);
        payload["model"] = request.Model;
        payload["query"] = request.Query;
        payload["documents"] = documents;
        if (request.TopN is not null) payload["top_n"] = request.TopN.Value;

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AIsaRerankJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AIsa rerank request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var ranking = root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(result => new RerankingRanking
            {
                Index = AIsaTryGetInt(result, "index") ?? 0,
                RelevanceScore = (float)(AIsaTryGetDouble(result, "relevance_score", "score") ?? 0d)
            }).OrderByDescending(result => result.RelevanceScore).ToList()
            : [];

        return new RerankingResponse
        {
            Ranking = ranking,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new RerankResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                Id = root.TryGetId(),
                ModelId = (AIsaTryGetString(root, "model") ?? request.Model).ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private static Dictionary<string, object?> AIsaJsonObjectToDictionary(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Where(property => property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                .ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

    private static string? AIsaTryGetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int? AIsaTryGetInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number : null;

    private static double? AIsaTryGetDouble(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)) return number;
        return null;
    }
}
