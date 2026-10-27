using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ZenMux;

public partial class ZenMuxProvider
{
    public async Task<RerankingResponse> RerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentNullException.ThrowIfNull(request.Documents);
        if (request.TopN is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "TopN must be greater than zero.");
        if (request.Documents.Values.ValueKind != JsonValueKind.Array
            || request.Documents.Values.GetArrayLength() == 0)
            throw new ArgumentException("At least one rerank document is required.", nameof(request));

        var type = request.Documents.Type.ToLowerInvariant();
        if (type is not ("text" or "image" or "video"))
            throw new NotSupportedException($"ZenMux reranking does not document '{type}' documents.");

        var parameters = new Dictionary<string, object?>();
        if (request.TopN.HasValue)
            parameters["top_n"] = request.TopN.Value;
        ReadZenMuxRerankOptions(request.ProviderOptions, parameters);

        var documents = request.Documents.Values.EnumerateArray().Select(value =>
        {
            if (type == "text")
            {
                if (value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Text rerank documents must be strings.", nameof(request));
                return (object?)value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Object)
                return value.Clone();
            if (value.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"{type} rerank documents must be strings or content objects.", nameof(request));
            return (object)new Dictionary<string, string> { [type] = value.GetString()! };
        }).ToArray();

        object query = type == "text"
            ? request.Query
            : new Dictionary<string, string> { ["text"] = request.Query };
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = new { query, documents },
            ["parameters"] = parameters
        };

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(raw)
                    ? $"ZenMux rerank request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"ZenMux rerank request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("ZenMux rerank response did not contain a results array.");

        var ranking = results.EnumerateArray().Select(result =>
        {
            if (!result.TryGetProperty("index", out var index) || !index.TryGetInt32(out var indexValue)
                || !result.TryGetProperty("relevance_score", out var score) || !score.TryGetSingle(out var scoreValue))
                throw new InvalidOperationException("ZenMux rerank response contained an invalid result.");
            return new RerankingRanking { Index = indexValue, RelevanceScore = scoreValue };
        }).ToArray();

        var metadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                usage = root.TryGetProperty("usage", out var usage) ? usage.Clone() : (JsonElement?)null
            }, JsonSerializerOptions.Web)
        };

        return new RerankingResponse
        {
            Ranking = ranking,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                Id = response.Headers.TryGetValues("x-request-id", out var requestIds)
                    ? requestIds.FirstOrDefault()
                    : null,
                ModelId = (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                    ? model.GetString()
                    : null)?.ToModelId(GetIdentifier()) ?? request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private static void ReadZenMuxRerankOptions(
        Dictionary<string, JsonElement>? providerOptions,
        Dictionary<string, object?> parameters)
    {
        if (providerOptions?.TryGetValue("zenmux", out var options) != true)
            return;
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.zenmux must be an object.", nameof(providerOptions));

        var source = options.TryGetProperty("parameters", out var nested)
            ? nested
            : options;
        if (source.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("ZenMux rerank parameters must be an object.", nameof(providerOptions));

        var supported = new HashSet<string>(StringComparer.Ordinal)
        {
            "top_n", "return_documents", "instruct", "fps"
        };
        foreach (var property in source.EnumerateObject())
        {
            if (!supported.Contains(property.Name))
                throw new NotSupportedException($"ZenMux reranking does not support parameter '{property.Name}'.");
            parameters[property.Name] = property.Value.Clone();
        }
    }
}
