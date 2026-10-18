using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Clarifai;

public sealed partial class ClarifaiProvider
{
    private async Task<IEnumerable<Model>> ListClarifaiModelsAsync(CancellationToken cancellationToken)
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
                        $"Clarifai model discovery failed with status {(int)response.StatusCode}: {error}",
                        null,
                        response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                    return (IEnumerable<Model>)[];

                return data.EnumerateArray()
                    .Select(ToClarifaiModel)
                    .OfType<Model>()
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ToClarifaiModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idElement.GetString()))
            return null;

        var id = idElement.GetString()!;
        return new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = id,
            OwnedBy = GetString(item, "owned_by") ?? string.Empty,
            Created = GetInt64(item, "created"),
            Type = "language",
            Metadata = item.EnumerateObject()
                .Where(property => !KnownModelProperties.Contains(property.Name))
                .ToDictionary(property => property.Name, property => property.Value.Clone())
        };
    }

    private static string? GetString(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetInt64(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out var result)
            ? result
            : null;

    private static readonly HashSet<string> KnownModelProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "object",
        "created",
        "owned_by"
    };
}
