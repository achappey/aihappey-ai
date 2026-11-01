using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    private static readonly JsonSerializerOptions RerankJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        // DeepInfra expects the model name in the path (without our provider prefix).
        if (request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));

        // DeepInfra schema requires queries and documents to have the same length.
        var docs = request.Documents.Values
            .EnumerateArray()
            .Select(d => d.ValueKind == JsonValueKind.String
                ? (d.GetString() ?? string.Empty)
                : d.GetRawText())
            .ToList();

        if (docs.Count == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));

        var queries = Enumerable.Repeat(request.Query, docs.Count).ToList();

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["queries"] = queries,
            ["documents"] = docs,
        };

        foreach (var (key, value) in GetAdditionalMetadata(metadata, "instruction", "service_tier", "queries", "documents"))
        {
            payload[key] = value;
        }

        var instruction = GetMetadataValue<string>(metadata, "instruction");
        if (!string.IsNullOrEmpty(instruction))
        {
            payload["instruction"] = instruction;
        }

        var serviceTier = GetMetadataValue<string>(metadata, "service_tier");
        if (!string.IsNullOrEmpty(serviceTier))
        {
            payload["service_tier"] = serviceTier;
        }

        // POST https://api.deepinfra.com/v1/inference/{model}
        using var req = new HttpRequestMessage(HttpMethod.Post, $"v1/inference/{request.Model}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, RerankJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("scores", out var scoresEl) || scoresEl.ValueKind != JsonValueKind.Array)
            throw new Exception("DeepInfra rerank response did not contain a 'scores' array.");

        var scores = scoresEl.EnumerateArray()
            .Select((s, i) => new
            {
                Index = i,
                Score = (float)s.GetDouble()
            })
            .ToList();

        if (scores.Count != docs.Count)
        {
            warnings.Add(new
            {
                type = "provider_response_mismatch",
                feature = "scores",
                details = $"DeepInfra returned {scores.Count} scores for {docs.Count} documents; using min length."
            });

            var min = Math.Min(scores.Count, docs.Count);
            scores = [.. scores.Take(min)];
        }

        var ranked = scores
            .OrderByDescending(s => s.Score)
            .Select(s => new RerankingRanking { Index = s.Index, RelevanceScore = s.Score });

        if (request.TopN is > 0)
            ranked = ranked.Take(request.TopN.Value);

        return new RerankingResponse
        {
            Ranking = [.. ranked],
            Warnings = warnings,
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

    private static Dictionary<string, object?> GetAdditionalMetadata(JsonElement metadata, params string[] mappedProperties)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return [];

        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("Provider metadata must be a JSON object.");

        var payload = new Dictionary<string, object?>();
        foreach (var property in metadata.EnumerateObject())
        {
            if (mappedProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static T? GetMetadataValue<T>(JsonElement metadata, string name)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return default;

        T? result = default;
        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                result = property.Value.Deserialize<T>(JsonSerializerOptions.Web);
        }

        return result;
    }
}
