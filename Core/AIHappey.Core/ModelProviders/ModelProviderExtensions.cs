using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderExtensions
{
    public static string CacheKeyFromApiKey(string apiKey)
    {
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash); // safe cache key
    }

    public static IEnumerable<Model> WithPricing(this IEnumerable<Model> models, string identifier)
    {
        var pricing = identifier.GetPricing() ?? [];
        List<Model> list = [.. models];

        foreach (var model in list)
        {
            if (pricing.TryGetValue(model.Id, out var value))
                model.Pricing = value;
        }

        return list;
    }


    public static async Task<ModelPricing?> ResolveCatalogPricingForModelAsync(this IModelProvider modelProvider,
        string? modelId, CancellationToken cancellationToken)
    {
        var model = await modelProvider.GetModel(modelId, cancellationToken);

        return model?.Pricing;
    }

    public static string GetCacheKey(this IModelProvider modelProvider, string? apiKey = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return $"models:{modelProvider.GetIdentifier()}";

        return $"models:{modelProvider.GetIdentifier()}:{CacheKeyFromApiKey(apiKey)}";
    }

    public static async Task<IEnumerable<Model>> ListModels(this IModelProvider modelProvider, string? key)
        => await Task.FromResult<IEnumerable<Model>>(modelProvider.GetIdentifier().GetModels());

    public static async Task<Model> GetModel(this IModelProvider modelProvider,
      string? modelId,
      CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        var models = await modelProvider.ListModels(cancellationToken);
        var model = models.FirstOrDefault(a => a.Id.EndsWith(modelId))
            ?? throw new ArgumentException(modelId);

        model.Type ??= model.Id.GuessModelType();

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
        var catalogFiles = TryReadCatalogFiles(provider);
        if (catalogFiles.Count == 0)
            return [];

        List<Model> models = [];
        foreach (var catalogFile in catalogFiles)
        {
            var modelResponseJson = File.ReadAllText(catalogFile);
            if (string.IsNullOrWhiteSpace(modelResponseJson))
                continue;

            var modelResponse = JsonSerializer.Deserialize<ModelResponse>(modelResponseJson, JsonSerializerOptions.Web);
            if (modelResponse?.Data is null)
                continue;

            models.AddRange(modelResponse.Data);
        }

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
        var json = TryReadPricingFile(provider);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        using var doc = JsonDocument.Parse(json);

        var result = new Dictionary<string, ModelPricing>();

        foreach (var item in doc.RootElement.EnumerateObject())
        {
            if (!item.Value.TryGetProperty("pricing", out var pricing))
                continue;

            var parsed = pricing.Deserialize<ModelPricing>(JsonSerializerOptions.Web);
            if (parsed is not null)
                result[item.Name] = parsed;
        }

        return result;
    }

    private static List<string> TryReadCatalogFiles(string provider)
    {
        var providersPath = Path.Combine(AppContext.BaseDirectory, "Catalog", "Models", "providers");
        List<string> catalogFiles = [];

        var legacyProviderFilePath = Path.Combine(providersPath, provider + ".json");
        if (File.Exists(legacyProviderFilePath))
            catalogFiles.Add(legacyProviderFilePath);

        var providerDirectoryPath = Path.Combine(providersPath, provider);
        if (Directory.Exists(providerDirectoryPath))
        {
            var providerDirectoryFiles = Directory
                .EnumerateFiles(providerDirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

            catalogFiles.AddRange(providerDirectoryFiles);
        }

        return catalogFiles;
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
