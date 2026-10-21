using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.PredictionGuard;

public sealed partial class PredictionGuardProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await asyncCacheHelper.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "models");
                request.Headers.Authorization = new("Bearer", key);
                using var response = await _client.SendAsync(request, ct);
                await EnsureSuccessAsync(response, ct);
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Prediction Guard model discovery must contain a data array.");

                return (IEnumerable<Model>)data.EnumerateArray()
                    .Select(ToModel).OfType<Model>().DistinctBy(model => model.Id).ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ToModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idElement.GetString()) ||
            !item.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object)
            return null;

        var type = HasCapability(capabilities, "chat_completion") || HasCapability(capabilities, "chat_with_image")
            ? "language" : HasCapability(capabilities, "embedding") ? "embedding"
            : HasCapability(capabilities, "rerank") ? "reranking" : null;
        if (type is null)
            return null;
        var id = idElement.GetString()!;
        var model = new Model
        {
            Id = $"{GetIdentifier()}/{id}",
            Name = id,
            OwnedBy = item.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String
                ? owner.GetString()! : null!,
            Type = type,
            Metadata = item.EnumerateObject()
                .Where(property => property.Name is not ("id" or "object" or "created" or "owned_by"))
                .ToDictionary(property => property.Name, property => property.Value.Clone())
        };
        if (item.TryGetProperty("created", out var created) &&
            created.ValueKind == JsonValueKind.Number && created.TryGetInt64(out var timestamp))
            model.Created = timestamp;
        return model;
    }

    private static bool HasCapability(JsonElement capabilities, string name)
        => capabilities.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Prediction Guard request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}",
                null, response.StatusCode);
    }
}
