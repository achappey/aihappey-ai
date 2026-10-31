using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Pinecone;

public partial class PineconeProvider
{
    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        var url = "rerank";

        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var metadata = GetProviderOptions(request);
        var payload = CreatePayload(request, metadata);

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

        var results = root.TryGetProperty("data", out var resultsEl)
            && resultsEl.ValueKind == JsonValueKind.Array
                ? resultsEl.EnumerateArray()
                    .Select(r => new RerankingRanking
                    {
                        Index = r.GetProperty("index").GetInt32(),
                        RelevanceScore = (float)r.GetProperty("score").GetDouble(),
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

    private static Dictionary<string, object?> CreatePayload(RerankingRequest request, JsonElement? metadata)
    {
        var payload = new Dictionary<string, object?>();

        if (metadata is { } rawMetadata)
        {
            foreach (var property in rawMetadata.EnumerateObject())
            {
                if (!IsKnownOption(property.Name))
                    payload[property.Name] = property.Value.Clone();
            }

            AddBooleanOption(payload, rawMetadata, "return_documents");
            AddRankFieldsOption(payload, rawMetadata);
            AddParametersOption(payload, rawMetadata);
        }

        payload["model"] = request.Model;
        payload["query"] = request.Query;
        payload["documents"] = request.Documents.Values;
        payload["top_n"] = request.TopN;
        return payload;
    }

    private JsonElement? GetProviderOptions(RerankingRequest request)
    {
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) != true
            || options.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (options.ValueKind != JsonValueKind.Object)
            throw new JsonException("Pinecone provider options must be a JSON object.");

        return options;
    }

    private static bool IsKnownOption(string name)
        => name.Equals("return_documents", StringComparison.OrdinalIgnoreCase)
            || name.Equals("rank_fields", StringComparison.OrdinalIgnoreCase)
            || name.Equals("parameters", StringComparison.OrdinalIgnoreCase);

    private static void AddBooleanOption(
        IDictionary<string, object?> payload,
        JsonElement metadata,
        string name)
    {
        var value = FindOption(metadata, name);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
            return;

        payload[name] = value.Value.Deserialize<bool?>(JsonOpts);
    }

    private static void AddRankFieldsOption(IDictionary<string, object?> payload, JsonElement metadata)
    {
        var value = FindOption(metadata, "rank_fields");
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
            return;

        var rankFields = value.Value.Deserialize<string[]>(JsonOpts);
        if (rankFields is { Length: > 0 })
            payload["rank_fields"] = rankFields;
    }

    private static void AddParametersOption(IDictionary<string, object?> payload, JsonElement metadata)
    {
        var value = FindOption(metadata, "parameters");
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
            return;

        payload["parameters"] = value.Value.Clone();
    }

    private static JsonElement? FindOption(JsonElement metadata, string name)
    {
        JsonElement? value = null;
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                value = property.Value;
        }

        return value;
    }
}
