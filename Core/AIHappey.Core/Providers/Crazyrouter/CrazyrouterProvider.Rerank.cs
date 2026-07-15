using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Text.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Crazyrouter;

public partial class CrazyrouterProvider
{
    private static readonly JsonSerializerOptions CrazyrouterRerankJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<RerankingResponse> CrazyrouterRerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request));
        if (request.Documents is null)
            throw new ArgumentException("Documents are required.", nameof(request));
        if (request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));

        var documents = request.Documents.Values
            .EnumerateArray()
            .Select(d => d.ValueKind switch
            {
                JsonValueKind.String => d.GetString() ?? string.Empty,
                JsonValueKind.Object when d.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String => textEl.GetString() ?? string.Empty,
                _ => throw new ArgumentException("Documents.values must contain strings or objects with a text field.", nameof(request))
            })
            .ToList();

        if (documents.Count == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));
        if (documents.Count > 100)
            throw new ArgumentException("Crazyrouter supports a maximum of 100 documents per rerank request.", nameof(request));
        if (request.TopN is <= 0)
            throw new ArgumentException("TopN must be >= 1 when provided.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var metadata = request.GetProviderMetadata<CrazyrouterRerankingProviderMetadata>(GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            ["documents"] = documents,
            ["top_n"] = request.TopN
        };

        if (metadata?.ReturnDocuments is not null)
            payload["return_documents"] = metadata.ReturnDocuments;

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, CrazyrouterRerankJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Crazyrouter rerank failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Crazyrouter rerank response did not contain a 'results' array.");

        var ranked = resultsEl
            .EnumerateArray()
            .Select(r => new RerankingRanking
            {
                Index = r.GetProperty("index").GetInt32(),
                RelevanceScore = (float)r.GetProperty("relevance_score").GetDouble(),
            })
            .ToList();

        return new RerankingResponse
        {
            Ranking = ranked,
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

    private sealed class CrazyrouterRerankingProviderMetadata
    {
        [JsonPropertyName("return_documents")]
        public bool? ReturnDocuments { get; set; }
    }
}
