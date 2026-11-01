using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Together;

public partial class TogetherProvider
{
    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        var url = "v1/rerank";

        ApplyAuthHeader();

        var now = DateTime.UtcNow;

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = CopyProviderMetadata(metadata, "top_n", "return_documents", "rank_fields");
        payload["model"] = request.Model;
        payload["query"] = request.Query;
        payload["documents"] = request.Documents.Values;
        if (request.TopN is not null)
            payload["top_n"] = request.TopN;

        var returnDocuments = GetProviderMetadataValue<bool?>(metadata, "return_documents");
        if (returnDocuments is not null)
        {
            payload["return_documents"] = returnDocuments;
        }

        var rankFields = GetProviderMetadataValue<string[]?>(metadata, "rank_fields");
        if (rankFields is { Length: > 0 })
        {
            payload["rank_fields"] = rankFields;
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

        return new RerankingResponse
        {
            Ranking = results,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
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
            throw new JsonException("Together provider metadata must be a JSON object.");

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
