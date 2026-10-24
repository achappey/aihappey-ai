using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Lightcone;

public sealed partial class LightconeProvider
{
    private async Task<IEnumerable<Model>> ListLightconeModelsAsync(CancellationToken cancellationToken)
    {
        var apiKey = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(apiKey))
            return GetIdentifier().GetModels();

        return await asyncCacheHelper.GetOrCreateAsync(
            this.GetCacheKey(apiKey),
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                request.Headers.Authorization = new("Bearer", apiKey);

                using var response = await _client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"Lightcone model discovery failed with status {(int)response.StatusCode}: {error}",
                        null,
                        response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("Lightcone model discovery returned an invalid model list.");
                }

                return data.EnumerateArray()
                    .Select(ToLightconeModel)
                    .OfType<Model>()
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ToLightconeModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            return null;
        }

        var id = idElement.GetString()!;
        var model = new Model
        {
            Id = id.StartsWith($"{GetIdentifier()}/", StringComparison.OrdinalIgnoreCase)
                ? id
                : id.ToModelId(GetIdentifier()),
            Object = GetString(item, "object") ?? "model",
            Created = GetInt64(item, "created"),
            OwnedBy = GetString(item, "owned_by") ?? "Tzafon",
            Name = GetString(item, "name") ?? id,
            Description = GetString(item, "description"),
            ContextWindow = GetInt32(item, "context_window"),
            MaxTokens = GetInt32(item, "max_tokens"),
            Type = GetString(item, "type") ?? "language",
            Tags = GetStringArray(item, "tags"),
            Metadata = item.EnumerateObject()
                .Where(property => !KnownProperties.Contains(property.Name))
                .ToDictionary(property => property.Name, property => property.Value.Clone())
        };

        return model;
    }

    private static string? GetString(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetInt64(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out var number)
            ? number
            : null;

    private static int? GetInt32(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var number)
            ? number
            : null;

    private static IEnumerable<string>? GetStringArray(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString()!)
                .ToArray()
            : null;

    private static readonly HashSet<string> KnownProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "object",
        "created",
        "owned_by",
        "name",
        "description",
        "context_window",
        "max_tokens",
        "type",
        "tags"
    };
}
