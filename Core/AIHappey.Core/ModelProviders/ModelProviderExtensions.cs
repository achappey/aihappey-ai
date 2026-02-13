using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderExtensions
{
    public static async Task<IEnumerable<Model>> ListModels(this IModelProvider modelProvider, string? key)
    => string.IsNullOrWhiteSpace(key)
        ? await Task.FromResult<IEnumerable<Model>>([])
        : await Task.FromResult<IEnumerable<Model>>(modelProvider.GetIdentifier().GetModels());

    public static async Task<Model> GetModel(this IModelProvider modelProvider,
      string? modelId,
      CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        var models = await modelProvider.ListModels(cancellationToken);
        var model = models.FirstOrDefault(a => a.Id.EndsWith(modelId))
            ?? throw new ArgumentException(modelId);

        return model;
    }

    public static Dictionary<string, Dictionary<string, object>?> CreateProviderMetadata(this IModelProvider modelProvider,
       Dictionary<string, object> metadata)
         => modelProvider.GetIdentifier().CreateProviderMetadata(metadata);

    public static Dictionary<string, Dictionary<string, object>?> CreateProviderMetadata(this string modelProviderId,
        Dictionary<string, object> metadata)
        => new()
        {
                { modelProviderId, metadata }
        };

    public static List<Model> GetModels(this string provider)
    {
        var modelResponseJson = TryReadCatalogFile(provider);
        if (string.IsNullOrWhiteSpace(modelResponseJson))
            return [];

        var modelResponse = JsonSerializer.Deserialize<ModelResponse>(modelResponseJson, JsonSerializerOptions.Web);
        var models = modelResponse?.Data?.ToList() ?? [];
        if (models.Count == 0)
            return [];

        var pricing = provider.GetPricing() ?? [];

        foreach (var model in models)
        {
            if (pricing.ContainsKey(model.Id))
            {
                model.Pricing = pricing[model.Id];
            }
        }

        return models;
    }


    public static Dictionary<string, ModelPricing>? GetPricing(this string provider)
    {
        var modelResponseJson = TryReadPricingFile(provider);
        if (string.IsNullOrWhiteSpace(modelResponseJson))
            return [];

        var modelResponse = JsonSerializer.Deserialize<Dictionary<string, ModelPricing>>(modelResponseJson, JsonSerializerOptions.Web);

        return modelResponse;
    }

    private static string? TryReadCatalogFile(string provider)
    {
        List<string> folders = ["Catalog", "Models", "providers", provider + ".json"];

        var path = Path.Combine(AppContext.BaseDirectory, string.Join(Path.DirectorySeparatorChar, folders));

        if (File.Exists(path))
            return File.ReadAllText(path);

        return null;
    }

    private static string? TryReadPricingFile(string provider)
    {
        List<string> folders = ["Catalog", "Pricing", "providers", provider + ".json"];

        var path = Path.Combine(AppContext.BaseDirectory, string.Join(Path.DirectorySeparatorChar, folders));

        if (File.Exists(path))
            return File.ReadAllText(path);

        return null;
    }

}
