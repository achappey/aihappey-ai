using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.GWDG;

public sealed partial class GWDGProvider
{
    private static readonly HashSet<string> KnownModelProperties = new(StringComparer.Ordinal)
    {
        "id", "object", "created", "owned_by", "name", "description"
    };

    private async Task<IEnumerable<Model>> ListGWDGModelsAsync(CancellationToken cancellationToken)
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
                        $"GWDG model discovery failed with status {(int)response.StatusCode}: {error}",
                        null, response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                    throw new JsonException("GWDG model discovery must contain a data array.");

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
            GetString(item, "id") is not { Length: > 0 } id ||
            string.IsNullOrWhiteSpace(id) ||
            !ContainsModality(item, "input", "text") ||
            !ContainsModality(item, "output", "text"))
            return null;

        var model = new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = GetString(item, "name") ?? id,
            OwnedBy = GetString(item, "owned_by")!,
            Description = GetString(item, "description"),
            Type = "language",
            Metadata = item.EnumerateObject()
                .Where(property => !KnownModelProperties.Contains(property.Name))
                .ToDictionary(property => property.Name, property => property.Value.Clone())
        };

        if (item.TryGetProperty("created", out var created) &&
            created.ValueKind == JsonValueKind.Number &&
            created.TryGetInt64(out var timestamp))
            model.Created = timestamp;

        return model;
    }

    private static bool ContainsModality(JsonElement item, string property, string modality)
        => item.TryGetProperty(property, out var values) &&
           values.ValueKind == JsonValueKind.Array &&
           values.EnumerateArray().Any(value =>
               value.ValueKind == JsonValueKind.String && value.GetString() == modality);

    private static string? GetString(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
