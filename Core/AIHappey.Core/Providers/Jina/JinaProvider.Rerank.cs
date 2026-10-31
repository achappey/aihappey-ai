using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Jina;

public partial class JinaProvider
{
    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        var url = "https://api.jina.ai/v1/rerank";

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

        var modelItem = await this.GetModel(request.Model, cancellationToken);
        var providerKey = GetIdentifier();

        decimal? totalTokens = null;

        if (root.TryGetProperty("usage", out var usageEl)
            && usageEl.ValueKind == JsonValueKind.Object
            && usageEl.TryGetProperty("total_tokens", out var totalTokensEl)
            && totalTokensEl.ValueKind == JsonValueKind.Number
            && totalTokensEl.TryGetDecimal(out var parsedTotalTokens))
        {
            totalTokens = parsedTotalTokens;
        }

        decimal? cost = null;

        if (totalTokens is not null && modelItem?.Pricing?.Input is not null)
        {
            cost = totalTokens.Value * modelItem.Pricing.Input;
        }

        return new RerankingResponse
        {
            Ranking = results,
            ProviderMetadata = providerKey
            .CreatePrimitiveProviderMetadata(
                root.TryGetProperty("usage", out var providerUsageEl)
                && providerUsageEl.ValueKind == JsonValueKind.Object ?
                    new
                    {
                        usage = providerUsageEl.Clone()
                    } : null, cost
            ),
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
            AddBooleanOption(payload, rawMetadata, "return_embeddings");
            AddBooleanOption(payload, rawMetadata, "truncation");
            AddIntegerOption(payload, rawMetadata, "max_doc_length");
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
            throw new JsonException("Jina provider options must be a JSON object.");

        return options;
    }

    private static bool IsKnownOption(string name)
        => name.Equals("return_documents", StringComparison.OrdinalIgnoreCase)
            || name.Equals("return_embeddings", StringComparison.OrdinalIgnoreCase)
            || name.Equals("truncation", StringComparison.OrdinalIgnoreCase)
            || name.Equals("max_doc_length", StringComparison.OrdinalIgnoreCase);

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

    private static void AddIntegerOption(
        IDictionary<string, object?> payload,
        JsonElement metadata,
        string name)
    {
        var value = FindOption(metadata, name);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
            return;

        payload[name] = value.Value.Deserialize<int?>(JsonOpts);
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
