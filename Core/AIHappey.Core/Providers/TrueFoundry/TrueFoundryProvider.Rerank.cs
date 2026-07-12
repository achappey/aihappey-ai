using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.TrueFoundry;

public partial class TrueFoundryProvider
{
    private readonly JsonSerializerOptions TrueFoundryJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<RerankingResponse> TrueFoundryRerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
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
        var payload = BuildTrueFoundryRerankPayload(request, metadata, warnings);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v2/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, TrueFoundryJsonOptions),
                Encoding.UTF8,
                MediaTypeHeaderValue.Parse(MediaTypeNames.Application.Json))
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"TrueFoundry rerank request failed ({(int)response.StatusCode})."
                : $"TrueFoundry rerank request failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var ranked = root.TryGetProperty("results", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Array
            ? resultsEl.EnumerateArray()
                .Select(ReadTrueFoundryRerankRanking)
                .OrderByDescending(ranking => ranking.RelevanceScore)
                .ToList()
            : [];

        if (!root.TryGetProperty("results", out _) || resultsEl.ValueKind != JsonValueKind.Array)
            warnings.Add(new { type = "provider_response_missing_field", feature = "results", details = "TrueFoundry rerank response did not contain a results array." });

        if (request.TopN is > 0)
            ranked = [.. ranked.Take(request.TopN.Value)];

        return new RerankingResponse
        {
            Ranking = ranked,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
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

    private Dictionary<string, object?> BuildTrueFoundryRerankPayload(RerankingRequest request, JsonElement metadata, List<object> warnings)
    {
        if (!string.Equals(request.Documents.Type, "text", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "unsupported", feature = "documents.type", details = "TrueFoundry rerank expects text document strings. Documents.values was forwarded as strings." });

        var payload = TrueFoundryJsonObjectToDictionary(metadata);

        payload["model"] = request.Model;
        payload["query"] = request.Query;
        payload["documents"] = ReadTrueFoundryRerankDocuments(request);

        if (request.TopN.HasValue)
            payload["top_n"] = request.TopN.Value;

        return payload;
    }

    private IReadOnlyList<string> ReadTrueFoundryRerankDocuments(RerankingRequest request)
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

    private RerankingRanking ReadTrueFoundryRerankRanking(JsonElement result)
        => new()
        {
            Index = TrueFoundryTryGetInt(result, "index") ?? 0,
            RelevanceScore = (float)(TrueFoundryTryGetDouble(result, "relevance_score", "relevanceScore", "score") ?? 0d)
        };

    private Dictionary<string, object?> TrueFoundryJsonObjectToDictionary(JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

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

    private void AddTrueFoundryMetadataFormFields(MultipartFormDataContent form, JsonElement metadata, params string[] skipNames)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (skipNames.Any(skipName => property.NameEquals(skipName)))
                continue;

            var value = TrueFoundryJsonElementToFormValue(property.Value);
            if (value is null)
                continue;

            form.Add(new StringContent(value, Encoding.UTF8), property.Name);
        }
    }

    private void AddTrueFoundryMultipartString(MultipartFormDataContent form, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        form.Add(new StringContent(value, Encoding.UTF8), name);
    }

    private string? TrueFoundryJsonElementToFormValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => value.GetRawText(),
            JsonValueKind.Object => value.GetRawText(),
            _ => value.GetRawText()
        };

    private string? TrueFoundryTryGetString(JsonElement element, params string[] propertyNames)
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

    private int? TrueFoundryTryGetInt(JsonElement element, params string[] propertyNames)
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
                && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number;
        }

        return null;
    }

    private double? TrueFoundryTryGetDouble(JsonElement element, params string[] propertyNames)
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
                && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return number;
        }

        return null;
    }

    private DateTime? ReadTrueFoundryUnixTimestamp(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
            return null;

        long? unixSeconds = null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
            unixSeconds = number;
        else if (property.ValueKind == JsonValueKind.String
                 && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
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
