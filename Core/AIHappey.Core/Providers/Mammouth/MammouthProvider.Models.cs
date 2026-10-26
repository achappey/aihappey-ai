using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Mammouth;

public sealed partial class MammouthProvider
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
                using var request = new HttpRequestMessage(HttpMethod.Get, "public/models");
                using var response = await _client.SendAsync(request, ct);
                await EnsureSuccessAsync(response, ct);
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Mammouth model discovery must contain a data array.");

                return (IEnumerable<Model>)data.EnumerateArray().Select(ToModel).OfType<Model>()
                    .DistinctBy(model => model.Id).ToArray();
            },
            baseTtl: TimeSpan.FromHours(4), jitterMinutes: 480, cancellationToken: cancellationToken);
    }

    private Model? ToModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()))
            return null;

        var nativeId = id.GetString()!;
        var type = nativeId.GuessModelType();
        if (type is not ("language" or "embedding" or "image"))
            return null;

        return new Model
        {
            Id = $"{GetIdentifier()}/{nativeId}",
            Name = nativeId,
            Type = type,
            OwnedBy = "Mammouth",
        };
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Mammouth request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}",
                null, response.StatusCode);
    }
}
