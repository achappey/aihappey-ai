using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.AbacusAI;

public sealed partial class AbacusAIProvider
{
    private async Task<IEnumerable<Model>> ListAbacusModelsAsync(
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var catalogModels = "abacusai".GetModels();
        if (string.IsNullOrWhiteSpace(apiKey))
            return catalogModels;

        var cacheKey = $"models:abacusai:{ModelProviderExtensions.CacheKeyFromApiKey(apiKey)}";
        return await memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();
                using var response = await _client.GetAsync("v1/models", ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"AbacusAI model-listing error ({(int)response.StatusCode}): {error}",
                        null,
                        response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!document.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                    throw new JsonException("AbacusAI model catalog must contain a data array.");

                var models = new Dictionary<string, Model>(StringComparer.Ordinal);
                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(idElement.GetString()))
                        continue;

                    var id = idElement.GetString()!;
                    var model = new Model
                    {
                        Id = id.ToModelId("abacusai"),
                        Name = ReadString(item, "name") ?? id,
                        OwnedBy = ReadString(item, "owned_by") ?? "Abacus.AI",
                        Description = ReadString(item, "description"),
                        Type = IsDecisionModel(id) ? "decision" : id.GuessModelType(),
                        Created = ReadInt64(item, "created")
                    };
                    models[model.Id] = model;
                }

                foreach (var catalogModel in catalogModels)
                {
                    if (models.TryGetValue(catalogModel.Id, out var discovered))
                    {
                        discovered.Type = "decision";
                        discovered.Name = catalogModel.Name;
                        discovered.Description ??= catalogModel.Description;
                    }
                    else
                    {
                        models[catalogModel.Id] = catalogModel;
                    }
                }

                return models.Values.ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static bool IsDecisionModel(string id)
        => id is "gpt-6-luna" or "jev-latest";

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static long? ReadInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out var value)
                ? value
                : null;

}
