using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.RouterPlus;

public sealed partial class RouterPlusProvider
{
    private const string Decider2BModel = "decider-2b";
    private const string Kev4BModel = "kev-4b";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        var cacheKey = this.GetCacheKey(key);
        return await memoryCache.GetOrCreateAsync<IEnumerable<Model>>(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();
                using var response = await _client.GetAsync("v1/models", ct);
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"RouterPlus model listing failed ({(int)response.StatusCode}): {raw}",
                        null,
                        response.StatusCode);

                using var document = JsonDocument.Parse(raw);
                if (!document.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                    throw new JsonException("RouterPlus model catalog must contain a data array.");

                var models = new Dictionary<string, Model>(StringComparer.Ordinal);
                foreach (var item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(idElement.GetString()))
                        continue;

                    var nativeId = idElement.GetString()!;
                    var id = NormalizeModelId(nativeId).ToModelId(GetIdentifier());
                    var model = new Model
                    {
                        Id = id,
                        Name = ReadString(item, "name") ?? nativeId,
                        OwnedBy = ReadString(item, "owned_by") ?? GetIdentifier(),
                        Type = ReadString(item, "type") ?? nativeId.GuessModelType(),
                        Description = ReadString(item, "description"),
                        ContextWindow = ReadInt32(item, "context_window"),
                        MaxTokens = ReadInt32(item, "max_output_tokens"),
                        Created = ReadInt64(item, "created"),
                        Tags = ReadStrings(item, "tags"),
                        Pricing = ReadPricing(item)
                    };
                    models[id] = model;
                }

                AddDocumentedDecisionModel(
                    models, Decider2BModel, "Decider 2B", 25_600,
                    "Free decision model; input above 25,600 tokens is truncated.");
                AddDocumentedDecisionModel(
                    models, Kev4BModel, "Kev 4B", 8_192,
                    "Free decision model; input above 8,192 tokens is refused.");
                return models.Values.ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static void AddDocumentedDecisionModel(
        IDictionary<string, Model> models,
        string modelName,
        string name,
        int contextWindow,
        string description)
    {
        var id = modelName.ToModelId("routerplus");
        models.TryGetValue(id, out var existing);
        models[id] = new Model
        {
            Id = id,
            Name = existing?.Name ?? name,
            OwnedBy = "routerplus",
            Type = "decision",
            Description = existing?.Description ?? description,
            ContextWindow = contextWindow,
            MaxTokens = existing?.MaxTokens,
            Created = existing?.Created,
            Tags = existing?.Tags,
            Pricing = new ModelPricing { Input = 0, Output = 0 }
        };
    }

    private static string NormalizeModelId(string model)
        => model.StartsWith("routerplus/", StringComparison.OrdinalIgnoreCase)
            ? model["routerplus/".Length..]
            : model;

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt32(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result) ? result : null;

    private static long? ReadInt64(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var result) ? result : null;

    private static IEnumerable<string>? ReadStrings(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : null;

    private static ModelPricing? ReadPricing(JsonElement element)
        => element.TryGetProperty("pricing", out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Deserialize<ModelPricing>(JsonSerializerOptions.Web)
            : null;
}
