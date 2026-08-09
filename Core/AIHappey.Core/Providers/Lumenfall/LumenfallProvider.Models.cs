using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Globalization;
using System.Text.Json;

namespace AIHappey.Core.Providers.Lumenfall;

public partial class LumenfallProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        var cacheKey = $"{this.GetCacheKey(key)}:merged-media-openrouter-language:v1";
        return await _memoryCache.GetOrCreateAsync<IEnumerable<Model>>(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();
                var mediaTask = FetchLumenfallMediaModelsAsync(ct);
                var languageTask = FetchOpenRouterLanguageModelsAsync(ct);
                await Task.WhenAll(mediaTask, languageTask);
                return [.. await mediaTask, .. await languageTask];
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<List<Model>> FetchLumenfallMediaModelsAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var error = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Lumenfall models API error ({(int)resp.StatusCode}): {error}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        List<Model> models = [];
        foreach (var element in EnumerateModelData(doc.RootElement))
        {
            var rawId = ModelString(element, "id");
            var type = ResolveLumenfallMediaType(element);
            if (string.IsNullOrWhiteSpace(rawId) || type is null)
                continue;

            models.Add(new Model
            {
                Id = rawId.ToModelId(GetIdentifier()),
                Name = ModelString(element, "name") ?? rawId,
                OwnedBy = ModelString(element, "owned_by") ?? ModelString(element, "creator_organization") ?? GetIdentifier(),
                Description = ModelString(element, "description"),
                Created = ModelInt64(element, "created"),
                Type = type,
                Tags = ReadStringArray(element, "tags")
            });
        }

        return models;
    }

    private async Task<List<Model>> FetchOpenRouterLanguageModelsAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models?output_modalities=all");
        using var resp = await _openRouterClient.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var error = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"OpenRouter models API error ({(int)resp.StatusCode}): {error}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        List<Model> models = [];
        foreach (var element in EnumerateModelData(doc.RootElement))
        {
            var rawId = ModelString(element, "id");
            if (string.IsNullOrWhiteSpace(rawId) || !IsOpenRouterLanguageModel(element))
                continue;

            models.Add(new Model
            {
                Id = rawId.ToModelId(GetIdentifier()),
                Name = ModelString(element, "name") ?? rawId,
                OwnedBy = rawId.Split('/').FirstOrDefault()?.TrimStart('~') ?? GetIdentifier(),
                Description = ModelString(element, "description"),
                Created = ModelInt64(element, "created"),
                ContextWindow = ModelInt32(element, "context_length"),
                MaxTokens = ReadOpenRouterMaxTokens(element),
                Pricing = ReadOpenRouterPricing(element),
                Type = "language"
            });
        }

        return models;
    }

    private static string? ResolveLumenfallMediaType(JsonElement element)
    {
        var modes = ReadStringArray(element, "modes").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tags = ReadStringArray(element, "tags").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isVideo = modes.Any(x => x.Contains("video", StringComparison.OrdinalIgnoreCase))
            || tags.Contains("video-generation");
        if (isVideo)
            return "video";

        var isImage = modes.Any(x => x.Contains("image", StringComparison.OrdinalIgnoreCase)
                || x.Contains("vector", StringComparison.OrdinalIgnoreCase))
            || tags.Contains("image-generation")
            || tags.Contains("image-editing")
            || tags.Contains("image-upscaling");
        return isImage ? "image" : null;
    }

    private static bool IsOpenRouterLanguageModel(JsonElement element)
    {
        if (!element.TryGetProperty("architecture", out var architecture) || architecture.ValueKind != JsonValueKind.Object)
            return false;
        var outputs = ReadStringArray(architecture, "output_modalities");
        return outputs.Contains("text", StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<JsonElement> EnumerateModelData(JsonElement root)
        => root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
            : [];

    private static IEnumerable<string> ReadStringArray(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray()
            : [];

    private static string? ModelString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ModelInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result
            : null;

    private static int? ModelInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;

    private static int? ReadOpenRouterMaxTokens(JsonElement element)
        => element.TryGetProperty("top_provider", out var provider) && provider.ValueKind == JsonValueKind.Object
            ? ModelInt32(provider, "max_completion_tokens")
            : null;

    private static ModelPricing? ReadOpenRouterPricing(JsonElement element)
    {
        if (!element.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object)
            return null;
        var input = ReadModelDecimal(pricing, "prompt");
        var output = ReadModelDecimal(pricing, "completion");
        if (input is null && output is null)
            return null;
        return new ModelPricing { Input = input ?? 0, Output = output ?? 0 };
    }

    private static decimal? ReadModelDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }
}
