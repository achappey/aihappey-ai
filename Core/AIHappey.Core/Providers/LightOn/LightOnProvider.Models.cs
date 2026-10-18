using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.LightOn;

public sealed partial class LightOnProvider
{
    private async Task<IEnumerable<Model>> ListLightOnModelsAsync(CancellationToken cancellationToken)
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await asyncCacheHelper.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/v3/instance/models");
                request.Headers.Authorization = new("Bearer", key);

                using var response = await _client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"LightOn model discovery failed with status {(int)response.StatusCode}: {error}",
                        null,
                        response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    return (IEnumerable<Model>)[];

                return document.RootElement.EnumerateArray()
                    .Select(ToLightOnModel)
                    .OfType<Model>()
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ToLightOnModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("enabled", out var enabledElement) ||
            enabledElement.ValueKind != JsonValueKind.True ||
            !item.TryGetProperty("technical_name", out var technicalNameElement) ||
            technicalNameElement.ValueKind != JsonValueKind.String ||
            !item.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var technicalName = technicalNameElement.GetString();
        var name = nameElement.GetString();
        var type = MapModelType(GetString(item, "model_type"));
        if (string.IsNullOrWhiteSpace(technicalName) ||
            string.IsNullOrWhiteSpace(name) ||
            type is null)
            return null;

        return new Model
        {
            Id = technicalName.ToModelId(GetIdentifier()),
            Name = name,
            OwnedBy = GetIdentifier(),
            Description = GetString(item, "description"),
            Type = type,
            Metadata = item.EnumerateObject()
                .Where(property => !KnownModelProperties.Contains(property.Name))
                .ToDictionary(property => property.Name, property => property.Value.Clone())
        };
    }

    private static string? MapModelType(string? modelType)
        => modelType?.Trim() switch
        {
            "Large Language Model" or "Vision Language Model" => "language",
            "Embedding Model" => "embedding",
            _ => null
        };

    private static string? GetString(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static readonly HashSet<string> KnownModelProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "technical_name",
        "name",
        "model_type",
        "enabled",
        "description"
    };
}
