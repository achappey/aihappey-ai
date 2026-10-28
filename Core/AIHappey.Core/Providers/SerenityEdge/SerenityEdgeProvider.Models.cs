using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SerenityEdge;

public sealed partial class SerenityEdgeProvider
{
    private async Task<IEnumerable<Model>> ListSerenityEdgeModelsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await _cache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models?format=openai");
                request.Headers.Authorization = new("Bearer", key);
                using var response = await _client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"Serenity Edge model discovery failed with status {(int)response.StatusCode}: {error}",
                        null,
                        response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("Serenity Edge model response did not contain a data list.");

                return (IEnumerable<Model>)data.EnumerateArray()
                    .Select(ToSerenityEdgeModel)
                    .OfType<Model>()
                    .DistinctBy(model => model.Id, StringComparer.Ordinal)
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ToSerenityEdgeModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idElement.GetString())
            || !item.TryGetProperty("endpoints", out var endpoints)
            || endpoints.ValueKind != JsonValueKind.Array)
            return null;

        var id = idElement.GetString()!;
        var endpointNames = endpoints.EnumerateArray()
            .Where(endpoint => endpoint.ValueKind == JsonValueKind.String)
            .Select(endpoint => endpoint.GetString())
            .ToHashSet(StringComparer.Ordinal);

        var type = endpointNames.Contains("/v1/decisions") ? "decision"
            : endpointNames.Contains("/v1/embeddings") ? "embedding"
            : endpointNames.Contains("/v1/rerank") ? "reranking"
            : endpointNames.Contains("/v1/chat/completions") || endpointNames.Contains("/v1/responses")
                ? "language"
                : null;
        if (type is null)
            return null;

        var model = item.Deserialize<Model>(JsonSerializerOptions.Web) ?? new Model();
        model.Id = id.ToModelId(GetIdentifier());
        model.Name = string.IsNullOrWhiteSpace(model.Name) ? id : model.Name;
        model.OwnedBy = string.IsNullOrWhiteSpace(model.OwnedBy) ? GetIdentifier() : model.OwnedBy;
        model.Type = type;
        return model;
    }
}
