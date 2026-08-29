using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider
{
    public async Task<RerankingResponse> RerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

       
        if (!string.Equals(request.Model, "qwen3-rerank", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Alibaba rerank model '{request.Model}' is not supported by this endpoint.");
        if (request.Documents?.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents must be an array of text strings.", nameof(request));

        var documents = request.Documents.Values.EnumerateArray().ToArray();
        if (documents.Length == 0 || documents.Any(item => item.ValueKind != JsonValueKind.String))
            throw new ArgumentException("Documents must contain one or more text strings.", nameof(request));

        ApplyAuthHeader();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["query"] = request.Query,
            ["documents"] = documents.Select(item => item.GetString()).ToArray(),
            ["top_n"] = request.TopN
        };

        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && options.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in options.EnumerateObject())
            {
                if (property.Name.Equals("instruct", StringComparison.OrdinalIgnoreCase))
                    payload["instruct"] = property.Value.Clone();
            }
        }

        var body = JsonSerializer.Serialize(payload, AlibabaRerankJsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "compatible-mode/v1/reranks")
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Alibaba rerank failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var ranking = root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(result => new RerankingRanking
            {
                Index = result.GetProperty("index").GetInt32(),
                RelevanceScore = result.GetProperty("relevance_score").GetSingle()
            }).ToArray()
            : [];

        return new RerankingResponse
        {
            Ranking = ranking,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new RerankResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                Id = root.TryGetProperty("id", out var id) ? id.GetString() : null,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

    private static readonly JsonSerializerOptions AlibabaRerankJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

}
