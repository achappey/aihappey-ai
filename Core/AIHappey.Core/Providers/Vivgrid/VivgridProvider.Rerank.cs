using System.Net.Mime;
using System.Text;
using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vivgrid;

public partial class VivgridProvider
{
    private async Task<RerankingResponse> RerankingRequestVivgrid(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request));
        if (request.Documents is null || request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));

        var documents = request.Documents.Values
            .EnumerateArray()
            .Select(document => document.ValueKind switch
            {
                JsonValueKind.String => document.GetString() ?? string.Empty,
                JsonValueKind.Object when document.TryGetProperty("text", out var textElement)
                    && textElement.ValueKind == JsonValueKind.String => textElement.GetString() ?? string.Empty,
                _ => document.GetRawText()
            })
            .ToList();

        if (documents.Count == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            ["documents"] = documents,
            ["top_n"] = request.TopN
        };

        MergeVivgridProviderOptions(payload, request.ProviderOptions, GetIdentifier());

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, VivgridJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vivgrid rerank failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        if (!root.TryGetProperty("results", out var resultsElement) || resultsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Vivgrid rerank response did not contain a results array.");

        var ranking = resultsElement
            .EnumerateArray()
            .Select(result => new RerankingRanking
            {
                Index = result.GetProperty("index").GetInt32(),
                RelevanceScore = (float)result.GetProperty("relevance_score").GetDouble()
            })
            .ToList();

        return new RerankingResponse
        {
            Ranking = ranking,
            Warnings = warnings,
            ProviderMetadata = BuildVivgridProviderMetadata(root),
            Response = new()
            {
                Timestamp = now,
                Id = root.TryGetId(),
                Headers = response.GetHeaders(),
                ModelId = root.TryGetString("model")?.ToModelId(GetIdentifier())
                    ?? request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }
}
