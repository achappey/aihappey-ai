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
                var catalogModels = GetIdentifier().GetModels();

                models.AddRange(await ListApiModelsAsync(ct));
                models.AddRange(catalogModels);
                models.AddRange(await ListSpeechVoiceShortcutModelsAsync(catalogModels, ct));
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

    private async Task<List<Model>> ListSpeechVoiceShortcutModelsAsync(
        IEnumerable<Model> catalogModels,
        CancellationToken cancellationToken)
    {
        var baseSpeechModels = catalogModels
            .Where(model => string.Equals(model.Type, "speech", StringComparison.OrdinalIgnoreCase))
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .ToArray();

        if (baseSpeechModels.Length == 0)
            return [];

        var voices = await ListSpeechVoicesAsync(cancellationToken);
        if (voices.Count == 0)
            return [];

        return [.. baseSpeechModels.SelectMany(baseModel => BuildSpeechVoiceShortcutModels(baseModel, voices))];
    }

    private async Task<IReadOnlyList<InworldVoice>> ListSpeechVoicesAsync(CancellationToken cancellationToken)
    {
        var voices = new List<InworldVoice>();

        await AddSpeechVoicePagesAsync(voices, filter: null, cancellationToken);
        await AddSpeechVoicePagesAsync(voices, filter: "community = \"true\"", cancellationToken);

        return [.. voices
            .Where(IsValidSpeechVoice)
            .GroupBy(voice => voice.VoiceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private async Task AddSpeechVoicePagesAsync(
        List<InworldVoice> voices,
        string? filter,
        CancellationToken cancellationToken)
    {
        const int pageSize = 2000;
        string? pageToken = null;

        do
        {
            var query = new List<string>
            {
                $"pageSize={pageSize}"
            };

            if (!string.IsNullOrWhiteSpace(filter))
                query.Add($"filter={Uri.EscapeDataString(filter)}");

            if (!string.IsNullOrWhiteSpace(pageToken))
                query.Add($"pageToken={Uri.EscapeDataString(pageToken)}");

            using var request = new HttpRequestMessage(HttpMethod.Get, $"voices/v1/voices?{string.Join('&', query)}");
            using var response = await _client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Inworld voices API failed ({(int)response.StatusCode}): {error}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            voices.AddRange(ParseSpeechVoices(root));
            pageToken = ReadString(root, "nextPageToken") ?? ReadString(root, "next_page_token");
        }
        while (!string.IsNullOrWhiteSpace(pageToken));
    }

    private IEnumerable<Model> BuildSpeechVoiceShortcutModels(Model baseModel, IEnumerable<InworldVoice> voices)
    {
        var baseModelId = StripInworldProviderPrefix(baseModel.Id);

        foreach (var voice in voices.Where(IsValidSpeechVoice))
        {
            yield return new Model
            {
                Id = $"{baseModelId}/{voice.VoiceId}".ToModelId(GetIdentifier()),
                OwnedBy = baseModel.OwnedBy,
                Type = "speech",
                Name = $"{baseModel.Name} · {BuildSpeechVoiceDisplayName(voice)}",
                Description = BuildSpeechVoiceShortcutDescription(baseModelId, voice),
                Tags = BuildSpeechVoiceShortcutTags(baseModelId, voice),
                Pricing = baseModel.Pricing
            };
        }
    }

    private static IReadOnlyList<InworldVoice> ParseSpeechVoices(JsonElement root)
    {
        JsonElement voicesElement = default;

        if (root.ValueKind == JsonValueKind.Array)
        {
            voicesElement = root;
        }
        else if (root.ValueKind == JsonValueKind.Object
                 && !TryGetProperty(root, "voices", out voicesElement))
        {
            return [];
        }

        if (voicesElement.ValueKind != JsonValueKind.Array)
            return [];

        var voices = new List<InworldVoice>();

        foreach (var item in voicesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var voiceId = ReadString(item, "voiceId")
                ?? ReadString(item, "voice_id")
                ?? ReadString(item, "id");

            if (string.IsNullOrWhiteSpace(voiceId))
                continue;

            voices.Add(new InworldVoice
            {
                VoiceId = voiceId.Trim(),
                Name = ReadString(item, "name"),
                DisplayName = ReadString(item, "displayName") ?? ReadString(item, "display_name"),
                Description = ReadString(item, "description"),
                LanguageCode = ReadString(item, "langCode") ?? ReadString(item, "lang_code"),
                Source = ReadString(item, "source"),
                Gender = ReadString(item, "gender"),
                AgeGroup = ReadString(item, "ageGroup") ?? ReadString(item, "age_group"),
                PromptLanguages = [.. ReadStringArray(item, "promptLanguages")
                    .Concat(ReadStringArray(item, "prompt_languages"))],
                Tags = [.. ReadStringArray(item, "tags")],
                Categories = [.. ReadStringArray(item, "categories")]
            });
        }

        return [.. voices
            .Where(IsValidSpeechVoice)
            .GroupBy(voice => voice.VoiceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private static string StripInworldProviderPrefix(string modelId)
    {
        const string providerPrefix = "inworld/";
        return modelId.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? modelId[providerPrefix.Length..]
            : modelId;
    }

    private static string BuildSpeechVoiceDisplayName(InworldVoice voice)
    {
        var name = string.IsNullOrWhiteSpace(voice.DisplayName)
            ? voice.VoiceId
            : voice.DisplayName.Trim();

        var language = voice.PromptLanguages.FirstOrDefault(language => !string.IsNullOrWhiteSpace(language))
            ?? voice.LanguageCode;

        if (string.IsNullOrWhiteSpace(language) && string.IsNullOrWhiteSpace(voice.Gender))
            return name;

        var details = new List<string>();

        if (!string.IsNullOrWhiteSpace(language))
            details.Add(language.Trim());

        if (!string.IsNullOrWhiteSpace(voice.Gender))
            details.Add(voice.Gender.Trim());

        return $"{name} ({string.Join(", ", details)})";
    }

    private static string BuildSpeechVoiceShortcutDescription(string baseModelId, InworldVoice voice)
    {
        var displayName = string.IsNullOrWhiteSpace(voice.DisplayName)
            ? voice.VoiceId
            : voice.DisplayName.Trim();

        var parts = new List<string>
        {
            $"Inworld TTS shortcut model for voice '{displayName}' ({voice.VoiceId}) on {baseModelId}."
        };

        if (!string.IsNullOrWhiteSpace(voice.Description))
            parts.Add(voice.Description.Trim());

        if (!string.IsNullOrWhiteSpace(voice.Source))
            parts.Add($"Source: {voice.Source.Trim()}.");

        return string.Join(' ', parts);
    }

    private static IEnumerable<string> BuildSpeechVoiceShortcutTags(string baseModelId, InworldVoice voice)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tts",
            "voice-shortcut",
            $"model:{NormalizeTagValue(baseModelId)}",
            $"voice:{NormalizeTagValue(voice.VoiceId)}"
        };

        AddTag(tags, "source", voice.Source);
        AddTag(tags, "gender", voice.Gender);
        AddTag(tags, "age-group", voice.AgeGroup);
        AddTag(tags, "language", voice.LanguageCode);

        foreach (var language in voice.PromptLanguages.Take(10))
            AddTag(tags, "prompt-language", language);

        foreach (var category in voice.Categories.Take(10))
            AddTag(tags, "category", category);

        foreach (var tag in voice.Tags.Take(20))
            AddTag(tags, "tag", tag);

        return tags;
    }

    private static void AddTag(HashSet<string> tags, string prefix, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            tags.Add($"{prefix}:{NormalizeTagValue(value)}");
    }

    private static bool IsValidSpeechVoice(InworldVoice voice)
        => !string.IsNullOrWhiteSpace(voice.VoiceId);

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

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed class InworldVoice
    {
        public string VoiceId { get; set; } = null!;
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? LanguageCode { get; set; }
        public string? Source { get; set; }
        public string? Gender { get; set; }
        public string? AgeGroup { get; set; }
        public IReadOnlyList<string> PromptLanguages { get; set; } = [];
        public IReadOnlyList<string> Tags { get; set; } = [];
        public IReadOnlyList<string> Categories { get; set; } = [];
    }
}
