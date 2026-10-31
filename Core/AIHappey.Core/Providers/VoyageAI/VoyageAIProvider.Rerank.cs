using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.VoyageAI;

public partial class VoyageAIProvider
{
    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        var url = "v1/rerank";

        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = new Dictionary<string, object?>();

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (!IsKnownOption(property.Name))
                    payload[property.Name] = property.Value.Clone();
            }

            var returnDocuments = ReadNullableBoolean(metadata, "return_documents");
            if (returnDocuments is not null)
                payload["return_documents"] = returnDocuments;

            var truncation = ReadNullableBoolean(metadata, "truncation");
            if (truncation is not null)
                payload["truncation"] = truncation;
        }
        else if (metadata.ValueKind != JsonValueKind.Undefined)
        {
            throw new JsonException("VoyageAI provider options must be a JSON object.");
        }

        payload["model"] = request.Model;
        payload["query"] = request.Query;
        payload["documents"] = request.Documents.Values;
        if (request.TopN is not null)
            payload["top_k"] = request.TopN;

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

    private static bool? ReadNullableBoolean(JsonElement metadata, string name)
    {
        if (!TryGetOption(metadata, name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return JsonSerializer.Deserialize<bool?>(value.GetRawText());
    }

    private static bool IsKnownOption(string name)
        => string.Equals(name, "return_documents", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "truncation", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetOption(JsonElement metadata, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in metadata.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            found = true;
        }

        return found;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
