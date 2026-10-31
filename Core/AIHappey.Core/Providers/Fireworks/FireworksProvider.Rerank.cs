using AIHappey.Common.Extensions;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Fireworks;

public partial class FireworksProvider
{
    private static readonly JsonSerializerOptions RerankJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        const string url = "v1/rerank";

        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request));
        if (request.Documents is null)
            throw new ArgumentException("Documents are required.", nameof(request));

        // Fireworks expects: documents: string[]
        if (request.Documents.Values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Documents.values must be an array.", nameof(request));

        var docs = request.Documents.Values
            .EnumerateArray()
            .Select(d => d.ValueKind == JsonValueKind.String
                ? (d.GetString() ?? string.Empty)
                : throw new ArgumentException("Documents.values must be an array of strings.", nameof(request)))
            .ToList();

        if (docs.Count == 0)
            throw new ArgumentException("At least one document is required.", nameof(request));

        // Fireworks schema: top_n must be >= 1 if provided.
        if (request.TopN is <= 0)
            throw new ArgumentException("TopN must be >= 1 when provided.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = new Dictionary<string, object?>();
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (!IsKnownOption(property.Name))
                    payload[property.Name] = property.Value.Clone();
            }

            var task = ReadNullableString(metadata, "task");
            if (!string.IsNullOrWhiteSpace(task))
                payload["task"] = task;

            var returnDocuments = ReadNullableBoolean(metadata, "return_documents");
            if (returnDocuments is not null)
                payload["return_documents"] = returnDocuments;
        }
        else if (metadata.ValueKind != JsonValueKind.Undefined)
        {
            throw new JsonException("Fireworks provider options must be a JSON object.");
        }

        payload["model"] = request.Model;
        payload["query"] = request.Query;
        payload["documents"] = docs;

        if (request.TopN is not null)
            payload["top_n"] = request.TopN;

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, RerankJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Fireworks rerank failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
        {
            throw new Exception("Fireworks rerank response did not contain a 'data' array");
        }

        var ranked = dataEl
            .EnumerateArray()
            .Select(r => new RerankingRanking
            {
                Index = r.GetProperty("index").GetInt32(),
                RelevanceScore = (float)r.GetProperty("relevance_score").GetDouble(),
            })
            .ToList();

        // Fireworks orders by highest first already, but we don't rely on that.
        ranked = [.. ranked.OrderByDescending(r => r.RelevanceScore)];

        // Keep parity with other providers: request.TopN also trims client-side.
        if (request.TopN is > 0)
            ranked = [.. ranked.Take(request.TopN.Value)];

        return new RerankingResponse
        {
            Ranking = ranked,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier()
                .CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                Id = root.TryGetId(),
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = raw
            }
        };
    }

    private static string? ReadNullableString(JsonElement metadata, string name)
    {
        if (!TryGetOption(metadata, name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return JsonSerializer.Deserialize<string?>(value.GetRawText());
    }

    private static bool? ReadNullableBoolean(JsonElement metadata, string name)
    {
        if (!TryGetOption(metadata, name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return JsonSerializer.Deserialize<bool?>(value.GetRawText());
    }

    private static bool IsKnownOption(string name)
        => string.Equals(name, "task", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "return_documents", StringComparison.OrdinalIgnoreCase);

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
}
