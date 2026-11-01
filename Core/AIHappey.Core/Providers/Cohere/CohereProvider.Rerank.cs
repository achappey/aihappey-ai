using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Cohere;

public partial class CohereProvider
{
    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        var url = "v2/rerank";
        ApplyAuthHeader();
        var now = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = CopyProviderMetadata(metadata, "priority", "max_tokens_per_doc");
        payload["model"] = request.Model;
        payload["query"] = request.Query;
        payload["documents"] = request.Documents.Values;
        payload["top_n"] = request.TopN;

        var priority = GetProviderMetadataValue<int?>(metadata, "priority");
        if (priority is not null)
        {
            payload["priority"] = priority;
        }

        var maxTokensPerDoc = GetProviderMetadataValue<int?>(metadata, "max_tokens_per_doc");
        if (maxTokensPerDoc is not null)
        {
            payload["max_tokens_per_doc"] = maxTokensPerDoc;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts),
                Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var errText = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            throw new Exception(errText);
        }

        using var doc = JsonDocument.Parse(errText);
        var root = doc.RootElement;

        var results = root.TryGetProperty("results", out var resultsEl)
            && resultsEl.ValueKind == JsonValueKind.Array
                ? resultsEl.EnumerateArray()
                    .Select(r => new RerankingRanking
                    {
                        Index = r.GetProperty("index").GetInt32(),
                        RelevanceScore = (float)r.GetProperty("relevance_score").GetDouble(),
                    })
                    .ToList()
                : [];

        decimal? searchUnits = null;

        if (root.TryGetProperty("meta", out var metaEl)
            && metaEl.ValueKind == JsonValueKind.Object
            && metaEl.TryGetProperty("billed_units", out var billedUnitsEl)
            && billedUnitsEl.ValueKind == JsonValueKind.Object
            && billedUnitsEl.TryGetProperty("search_units", out var searchUnitsEl)
            && searchUnitsEl.ValueKind == JsonValueKind.Number
            && searchUnitsEl.TryGetDecimal(out var parsedSearchUnits))
        {
            searchUnits = parsedSearchUnits;
        }

        decimal? cost = null;

        if (searchUnits is not null)
        {
            try
            {
                var rerankModel = await this.GetModel(request.Model, cancellationToken);
                if (rerankModel.Pricing?.Input is not null)
                    cost = searchUnits.Value * rerankModel.Pricing.Input;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Pricing enrichment must not fail a completed reranking request.
            }
        }

        return new RerankingResponse
        {
            Ranking = results,
            ProviderMetadata = GetIdentifier()
            .CreatePrimitiveProviderMetadata(
                root.TryGetProperty("meta", out var cohereMetaEl)
                && cohereMetaEl.ValueKind == JsonValueKind.Object ? new
                {
                    meta = cohereMetaEl.Clone()
                } : null,
                cost
            ),
            Response = new()
            {
                Timestamp = now,
                Id = root.TryGetId(),
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };

    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static Dictionary<string, object?> CopyProviderMetadata(
        JsonElement metadata,
        params string[] mappedProperties)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return [];

        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("Cohere provider metadata must be a JSON object.");

        var mappedPropertyNames = new HashSet<string>(mappedProperties, StringComparer.OrdinalIgnoreCase);
        return metadata.EnumerateObject()
            .Where(property => !mappedPropertyNames.Contains(property.Name))
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
    }

    private static T? GetProviderMetadataValue<T>(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return default;

        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                return property.Value.Deserialize<T>(JsonSerializerOptions.Web);
        }

        return default;
    }
}
