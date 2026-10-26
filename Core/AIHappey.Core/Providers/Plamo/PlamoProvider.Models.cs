using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Plamo;

public sealed partial class PlamoProvider
{
    private static readonly HashSet<string> KnownModelProperties = new(StringComparer.Ordinal)
    {
        "id", "object", "created", "owned_by"
    };

    private async Task<IEnumerable<Model>> ListPlamoModelsAsync(CancellationToken cancellationToken)
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await asyncCacheHelper.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                request.Headers.Authorization = new("Bearer", key);
                using var response = await _client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"PLaMo model discovery failed with status {(int)response.StatusCode}: {error}",
                        null, response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                    throw new JsonException("PLaMo model discovery must contain a data array.");

                return (IEnumerable<Model>)data.EnumerateArray()
                    .Select(ToModel)
                    .OfType<Model>()
                    .DistinctBy(model => model.Id)
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ToModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idElement.GetString()))
            return null;

        var id = idElement.GetString()!;
        var model = new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = id,
            OwnedBy = item.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String
                ? owner.GetString()! : null!,
            Type = "language"
        };

        if (item.TryGetProperty("created", out var created) &&
            created.ValueKind == JsonValueKind.Number &&
            created.TryGetInt64(out var timestamp))
            model.Created = timestamp;

        return model;
    }
}
