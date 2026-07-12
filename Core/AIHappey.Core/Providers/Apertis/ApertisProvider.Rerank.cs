using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Apertis;

public partial class ApertisProvider
{
    private static readonly JsonSerializerOptions ApertisRerankJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<RerankingResponse> ApertisRerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
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
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildApertisRerankPayload(request, metadata, warnings);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ApertisRerankJsonOptions),
                Encoding.UTF8,
                MediaTypeHeaderValue.Parse(MediaTypeNames.Application.Json))
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Apertis rerank request failed ({(int)response.StatusCode})."
                : $"Apertis rerank request failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        var ranked = root.TryGetProperty("results", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Array
            ? resultsEl.EnumerateArray()
                .Select(ReadApertisRerankRanking)
                .OrderByDescending(r => r.RelevanceScore)
                .ToList()
            : [];

        if (!root.TryGetProperty("results", out _) || resultsEl.ValueKind != JsonValueKind.Array)
            warnings.Add(new { type = "provider_response_missing_field", feature = "results", details = "Apertis rerank response did not contain a results array." });

        if (request.TopN is > 0)
            ranked = [.. ranked.Take(request.TopN.Value)];

        return new RerankingResponse
        {
            Ranking = ranked,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                Id = root.TryGetId(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private static Dictionary<string, object?> BuildApertisRerankPayload(RerankingRequest request, JsonElement metadata, List<object> warnings)
    {
        if (!string.Equals(request.Documents.Type, "text", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "unsupported", feature = "documents.type", details = "Apertis rerank expects text document strings. Documents.values was forwarded as strings." });

        var payload = ApertisJsonObjectToDictionary(metadata);

        payload["model"] = request.Model;
        payload["query"] = request.Query;
        payload["documents"] = ReadApertisRerankDocuments(request);

        return payload;
    }

    private static IReadOnlyList<string> ReadApertisRerankDocuments(RerankingRequest request)
    {
        if (request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));

        var documents = request.Documents.Values
            .EnumerateArray()
            .Select(document => document.ValueKind == JsonValueKind.String
                ? document.GetString() ?? string.Empty
                : throw new ArgumentException("Documents.values must be an array of strings.", nameof(request)))
            .ToList();

        if (documents.Count == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));

        return documents;
    }

    private static RerankingRanking ReadApertisRerankRanking(JsonElement result)
        => new()
        {
            Index = ApertisTryGetInt(result, "index") ?? 0,
            RelevanceScore = (float)(ApertisTryGetDouble(result, "relevance_score", "relevanceScore", "score") ?? 0d)
        };

    private static Dictionary<string, object?> ApertisJsonObjectToDictionary(JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>();

        if (metadata.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static string? ApertisTryGetString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.String)
                return property.GetString();

            if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return property.ToString();
        }

        return null;
    }

    private static int? ApertisTryGetInt(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
                return number;

            if (property.ValueKind == JsonValueKind.String
                && int.TryParse(property.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number))
                return number;
        }

        return null;
    }

    private static double? ApertisTryGetDouble(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
                return number;

            if (property.ValueKind == JsonValueKind.String
                && double.TryParse(property.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number))
                return number;
        }

        return null;
    }

    private static DateTime? ReadApertisUnixTimestamp(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
            return null;

        long? unixSeconds = null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
            unixSeconds = number;
        else if (property.ValueKind == JsonValueKind.String
                 && long.TryParse(property.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number))
            unixSeconds = number;

        if (!unixSeconds.HasValue)
            return null;

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).UtcDateTime;
        }
        catch
        {
            return null;
        }
    }
}
