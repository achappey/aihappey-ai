using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Globalization;
using System.Text.Json;

namespace AIHappey.Core.Providers.Inworld;

public partial class InworldProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                var models = new List<Model>();
                models.AddRange(await ListApiModelsAsync(ct));
                models.AddRange(GetIdentifier().GetModels());
                models.AddRange(await ListRouterShortcutModelsAsync(ct));

                return DeduplicateModels(models);
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static readonly IReadOnlyDictionary<string, string> InworldProviderIds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "anthropic", "SERVICE_PROVIDER_ANTHROPIC" },
            { "cerebras", "SERVICE_PROVIDER_CEREBRAS" },
            { "deepinfra", "SERVICE_PROVIDER_DEEPINFRA" },
            { "fireworks", "SERVICE_PROVIDER_FIREWORKS" },
            { "google", "SERVICE_PROVIDER_GOOGLE" },
            { "groq", "SERVICE_PROVIDER_GROQ" },
            { "mistral", "SERVICE_PROVIDER_MISTRAL" },
            { "openai", "SERVICE_PROVIDER_OPENAI" },
            { "tenstorrent", "SERVICE_PROVIDER_TENSTORRENT" },
            { "xai", "SERVICE_PROVIDER_XAI" },
        };

    private async Task<List<Model>> ListApiModelsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "llm/v1alpha/models");
        using var response = await _client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Inworld models API failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("models", out var modelsElement)
            || modelsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<Model>();

        foreach (var modelElement in modelsElement.EnumerateArray())
        {
            if (modelElement.ValueKind != JsonValueKind.Object)
                continue;

            var rawModel = ReadString(modelElement, "model");
            var provider = ReadString(modelElement, "provider");

            if (string.IsNullOrWhiteSpace(rawModel) || string.IsNullOrWhiteSpace(provider))
                continue;

            modelElement.TryGetProperty("spec", out var specElement);
            modelElement.TryGetProperty("pricing", out var pricingElement);

            models.Add(new Model
            {
                Id = BuildInworldProviderModelId(rawModel, provider),
                Name = rawModel,
                Description = BuildApiModelDescription(rawModel, provider, ReadString(modelElement, "modelCreator"), ReadBoolean(modelElement, "isSupported")),
                Type = "language",
                OwnedBy = ReadString(modelElement, "modelCreator") ?? provider,
                ContextWindow = ReadInt32(specElement, "contextLength"),
                MaxTokens = ReadInt32(specElement, "maxCompletionTokens"),
                Pricing = BuildPricing(pricingElement),
                Tags = BuildApiModelTags(provider, ReadString(modelElement, "modelCreator"), specElement, ReadBoolean(modelElement, "isSupported"))
            });
        }

        return models;
    }

    private async Task<List<Model>> ListRouterShortcutModelsAsync(CancellationToken cancellationToken)
    {
        var models = new List<Model>();
        string? pageToken = null;

        do
        {
            var path = string.IsNullOrWhiteSpace(pageToken)
                ? "router/v1/routers?page_size=100"
                : $"router/v1/routers?page_size=100&page_token={Uri.EscapeDataString(pageToken)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using var response = await _client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Inworld routers API failed ({(int)response.StatusCode}): {error}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            if (root.TryGetProperty("routers", out var routersElement)
                && routersElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var routerElement in routersElement.EnumerateArray())
                {
                    var routerName = ReadString(routerElement, "name");
                    if (string.IsNullOrWhiteSpace(routerName))
                        continue;

                    var displayName = ReadString(routerElement, "displayName");
                    models.Add(new Model
                    {
                        Id = routerName.ToModelId(GetIdentifier()),
                        Name = string.IsNullOrWhiteSpace(displayName) ? routerName : displayName,
                        Type = "language",
                        OwnedBy = "Inworld",
                        Description = BuildRouterShortcutDescription(routerName, displayName, routerElement),
                        Tags = BuildRouterShortcutTags(routerName, routerElement)
                    });
                }
            }

            pageToken = ReadString(root, "next_page_token");
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return models;
    }

    private List<Model> DeduplicateModels(IEnumerable<Model> models)
        => models
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    private string BuildInworldProviderModelId(string rawModel, string provider)
        => $"{GetIdentifier()}/{NormalizeProviderSegment(provider)}/{rawModel.Trim()}";

    private static string NormalizeProviderSegment(string provider)
    {
        var trimmed = provider.Trim();

        if (trimmed.StartsWith("SERVICE_PROVIDER_", StringComparison.OrdinalIgnoreCase))
            return trimmed.ToUpperInvariant();

        return InworldProviderIds.TryGetValue(trimmed, out var knownProvider)
            ? knownProvider
            : trimmed;
    }

    private static string? BuildApiModelDescription(string model, string provider, string? modelCreator, bool? isSupported)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(modelCreator))
            parts.Add($"Creator: {modelCreator}");

        if (!string.IsNullOrWhiteSpace(provider)
            && !string.Equals(provider, modelCreator, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"Hosted by: {provider}");
        }

        if (isSupported is false)
            parts.Add("Marked unsupported by Inworld");

        if (parts.Count == 0)
            return null;

        return $"{model}. {string.Join(". ", parts)}.";
    }

    private static ModelPricing? BuildPricing(JsonElement pricingElement)
    {
        var input = ReadDecimal(pricingElement, "prompt");
        var output = ReadDecimal(pricingElement, "completion");

        if (input is null && output is null)
            return null;

        return new ModelPricing
        {
            Input = input ?? 0m,
            Output = output ?? 0m
        };
    }

    private static IEnumerable<string>? BuildApiModelTags(string provider, string? modelCreator, JsonElement specElement, bool? isSupported)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"provider:{NormalizeTagValue(provider)}"
        };

        if (!string.IsNullOrWhiteSpace(modelCreator))
            tags.Add($"creator:{NormalizeTagValue(modelCreator)}");

        foreach (var modality in ReadStringArray(specElement, "inputModalities"))
            tags.Add($"input:{NormalizeTagValue(modality)}");

        foreach (var modality in ReadStringArray(specElement, "outputModalities"))
            tags.Add($"output:{NormalizeTagValue(modality)}");

        foreach (var parameter in ReadStringArray(specElement, "supportedParameters"))
            tags.Add($"parameter:{NormalizeTagValue(parameter)}");

        if (TryGetProperty(specElement, "capabilities", out var capabilitiesElement))
        {
            if (ReadBoolean(capabilitiesElement, "functionCalling") == true)
            {
                tags.Add("tools");
                tags.Add("capability:function-calling");
            }

            if (ReadBoolean(capabilitiesElement, "webSearch") == true)
                tags.Add("web-search");
            if (ReadBoolean(capabilitiesElement, "reasoning") == true)
                tags.Add("reasoning");
            if (ReadBoolean(capabilitiesElement, "promptCaching") == true)
                tags.Add("prompt-caching");
            if (ReadBoolean(capabilitiesElement, "responseSchema") == true)
                tags.Add("structured-outputs");
            if (ReadBoolean(capabilitiesElement, "vision") == true)
                tags.Add("vision");
        }

        tags.Add(isSupported is false ? "unsupported" : "supported");

        return tags.Count == 0 ? null : [.. tags];
    }

    private static string BuildRouterShortcutDescription(string routerName, string? displayName, JsonElement routerElement)
    {
        var routeCount = 0;
        if (TryGetProperty(routerElement, "routes", out var routesElement) && routesElement.ValueKind == JsonValueKind.Array)
            routeCount = routesElement.GetArrayLength();

        var hasDefaultRoute = TryGetProperty(routerElement, "defaultRoute", out var defaultRouteElement)
            && defaultRouteElement.ValueKind == JsonValueKind.Object;

        var label = string.IsNullOrWhiteSpace(displayName) ? routerName : displayName;
        return $"Inworld router shortcut model for predefined route policy '{label}' (router id: {routerName}, conditional routes: {routeCount}, default route: {(hasDefaultRoute ? "yes" : "no")}).";
    }

    private static IEnumerable<string>? BuildRouterShortcutTags(string routerName, JsonElement routerElement)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "router",
            "shortcut",
            "routing-policy",
            $"router:{NormalizeTagValue(routerName)}"
        };

        if (TryGetProperty(routerElement, "routes", out var routesElement) && routesElement.ValueKind == JsonValueKind.Array)
        {
            if (routesElement.GetArrayLength() > 0)
                tags.Add("conditional-routing");

            foreach (var routeElement in routesElement.EnumerateArray())
            {
                if (TryGetProperty(routeElement, "route", out var innerRoute)
                    && TryGetProperty(innerRoute, "route_id", out var routeIdElement)
                    && routeIdElement.ValueKind == JsonValueKind.String)
                {
                    var routeId = routeIdElement.GetString();
                    if (!string.IsNullOrWhiteSpace(routeId))
                        tags.Add($"route:{NormalizeTagValue(routeId)}");
                }
            }
        }

        if (TryGetProperty(routerElement, "defaultRoute", out var defaultRouteElement)
            && defaultRouteElement.ValueKind == JsonValueKind.Object)
        {
            tags.Add("default-route");

            if (TryGetProperty(defaultRouteElement, "route_id", out var routeIdElement)
                && routeIdElement.ValueKind == JsonValueKind.String)
            {
                var routeId = routeIdElement.GetString();
                if (!string.IsNullOrWhiteSpace(routeId))
                    tags.Add($"route:{NormalizeTagValue(routeId)}");
            }
        }

        return tags.Count == 0 ? null : [.. tags];
    }

    private static string NormalizeTagValue(string value)
        => value.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');

    private static string? ReadString(JsonElement element, string propertyName)
        => TryGetProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInt32(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
            return number;

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number)
        {
            if (property.TryGetDecimal(out var decimalValue))
                return decimalValue;

            return Convert.ToDecimal(property.GetDouble(), CultureInfo.InvariantCulture);
        }

        if (property.ValueKind == JsonValueKind.String
            && decimal.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool? ReadBoolean(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static IEnumerable<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return [];

        return property
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .OfType<string>()
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value))
            return true;

        value = default;
        return false;
    }
}
