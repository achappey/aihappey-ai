using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Infermatic;

public sealed partial class InfermaticProvider
{
    private static readonly HashSet<string> KnownModelProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "object",
        "created",
        "owned_by"
    };

    private async Task<IEnumerable<Model>> ListInfermaticModelsAsync(CancellationToken cancellationToken)
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
                        $"Infermatic model discovery failed with status {(int)response.StatusCode}: {error}",
                        null,
                        response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                    return (IEnumerable<Model>)[];

                return data.EnumerateArray()
                    .Select(ToModel)
                    .OfType<Model>()
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
            OwnedBy = GetString(item, "owned_by") ?? GetIdentifier(),
            Type = "language"
        };

        if (item.TryGetProperty("created", out var createdElement) &&
            createdElement.ValueKind == JsonValueKind.Number &&
            createdElement.TryGetInt64(out var created))
            model.Created = created;

        return model;
    }

    private static string? GetString(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
