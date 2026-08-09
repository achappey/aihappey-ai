using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EUrouter;

public partial class EUrouterProvider
{
    private static readonly JsonSerializerOptions EUrouterRerankJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RerankingResponse> RerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);

        if (request.Documents is null)
            throw new ArgumentException("Documents are required.", nameof(request));

        if (request.TopN is <= 0)
            throw new ArgumentException("TopN must be >= 1 when provided.", nameof(request));

        var documents = ReadEUrouterRerankDocuments(request);
        List<object> warnings = [];

        if (!string.Equals(request.Documents.Type, "text", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "documents.type",
                details = "EUrouter rerank is text-only. Documents.values is forwarded as strings."
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            ["documents"] = documents
        };

        if (request.TopN is not null)
            payload["top_n"] = request.TopN;

        MergeEUrouterRerankProviderOptions(payload, request);
        ApplyAuthHeader();
        var timestamp = DateTime.UtcNow;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, EUrouterRerankJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"EUrouter rerank request failed ({(int)response.StatusCode})."
                : $"EUrouter rerank request failed ({(int)response.StatusCode}): {raw}");
        }

        using var responseDocument = JsonDocument.Parse(raw);
        var root = responseDocument.RootElement.Clone();
        List<RerankingRanking> ranking = [];

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            ranking = results.EnumerateArray()
                .Select(ReadEUrouterRerankResult)
                .OrderByDescending(result => result.RelevanceScore)
                .ToList();
        }
        else
        {
            warnings.Add(new
            {
                type = "provider_response_missing_field",
                feature = "results",
                details = "EUrouter rerank response did not contain a 'results' array."
            });
        }

        if (request.TopN is > 0)
            ranking = [.. ranking.Take(request.TopN.Value)];

        var providerMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(
            root.TryGetProperty("usage", out var usage) ? new { usage = usage.Clone() } : new { });

        if (root.TryGetProperty("usage", out usage)
            && TryReadEUrouterRerankDecimal(usage, "cost", out var cost))
        {
            providerMetadata["gateway"] = JsonSerializer.SerializeToElement(
                new { cost },
                JsonSerializerOptions.Web);
        }

        return new RerankingResponse
        {
            Ranking = ranking,
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = timestamp,
                Headers = response.GetHeaders(),
                Id = root.TryGetId(),
                ModelId = (ReadEUrouterRerankString(root, "model") ?? request.Model).ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private static IReadOnlyList<string> ReadEUrouterRerankDocuments(RerankingRequest request)
    {
        if (request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));

        var documents = request.Documents.Values.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : throw new ArgumentException("Documents.values must contain strings.", nameof(request)))
            .ToList();

        if (documents.Count == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));

        if (documents.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Documents.values must contain non-empty strings.", nameof(request));

        return documents;
    }

    private static void MergeEUrouterRerankProviderOptions(
        IDictionary<string, object?> payload,
        RerankingRequest request)
    {
        if (request.ProviderOptions is null
            || !request.ProviderOptions.TryGetValue("eurouter", out var options)
            || options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in options.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private static RerankingRanking ReadEUrouterRerankResult(JsonElement result)
        => new()
        {
            Index = result.TryGetProperty("index", out var index) && index.TryGetInt32(out var indexValue)
                ? indexValue
                : 0,
            RelevanceScore = result.TryGetProperty("relevance_score", out var score)
                && score.TryGetSingle(out var scoreValue)
                    ? scoreValue
                    : 0
        };

    private static string? ReadEUrouterRerankString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadEUrouterRerankDecimal(
        JsonElement element,
        string propertyName,
        out decimal value)
    {
        value = 0m;

        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number)
            return property.TryGetDecimal(out value);

        return property.ValueKind == JsonValueKind.String
            && decimal.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
    }
}
