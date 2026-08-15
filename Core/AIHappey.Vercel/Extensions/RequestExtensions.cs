using System.Text.Json;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Vercel.Extensions;

public static class RequestExtensions
{
    public static T? GetProviderMetadata<T>(this TranscriptionRequest request, string providerId)
    {
        return request.ProviderOptions.GetProviderMetadata<T>(providerId);
    }

    public static T? GetProviderMetadata<T>(this SpeechRequest request, string providerId)
    {
        return request.ProviderOptions.GetProviderMetadata<T>(providerId);
    }

    public static T? GetProviderMetadata<T>(this ImageRequest request, string providerId)
    {
        return request.ProviderOptions.GetProviderMetadata<T>(providerId);
    }

    public static T? GetProviderMetadata<T>(this VideoRequest request, string providerId)
    {
        return request.ProviderOptions.GetProviderMetadata<T>(providerId);
    }

    public static T? GetProviderMetadata<T>(this RerankingRequest request, string providerId)
    {
        return request.ProviderOptions.GetProviderMetadata<T>(providerId);
    }

    private static T? GetProviderMetadata<T>(this Dictionary<string, JsonElement>? providerOptions, string providerId)
    {
        if (providerOptions is null)
            return default;

        if (!providerOptions.TryGetValue(providerId, out JsonElement element))
            return default;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }

    public static AIRequest ToUnifiedRequest(this ChatRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var inputItems = request.Messages?
            .Where(static message => message.Role != Role.system)
            .Select(a => a.ToUnifiedInputItem())
            .ToList() ?? [];
        var instructions = request.Messages is null
            ? null
            : string.Join("\n\n", request.Messages
                .Where(static message => message.Role == Role.system)
                .SelectMany(static message => message.Parts.OfType<TextUIPart>())
                .Select(static part => part.Text)
                .Where(static text => !string.IsNullOrWhiteSpace(text)));
        var providerMetadata = BuildProviderMetadataWithHistoricalContainer(request, providerId);

        return new AIRequest
        {
            ProviderId = providerId,
            Model = request.Model,
            Id = request.Id,
            Verbosity = request.Verbosity,
            Instructions = string.IsNullOrWhiteSpace(instructions) ? null : instructions,
            ResponseFormat = request.ResponseFormat,
            Input = new AIInput
            {
                Items = inputItems
            },
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxOutputTokens,
            MaxToolCalls = request.MaxToolCalls,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Select(ToUnifiedTool).ToList(),
            Headers = request.Headers,
            Metadata = providerMetadata?.ToDictionary(p => p.Key, p => (object?)p.Value)
        };
    }

    private static Dictionary<string, JsonElement>? BuildProviderMetadataWithHistoricalContainer(
        ChatRequest request,
        string providerId)
    {
        var metadata = request.ProviderMetadata?
            .ToDictionary(entry => entry.Key, entry => entry.Value.Clone());

        if (metadata?.TryGetValue(providerId, out var explicitProviderMetadata) == true
            && TryGetMeaningfulProperty(explicitProviderMetadata, "container", out _))
        {
            return metadata;
        }

        var historicalContainer = FindHistoricalAssistantContainer(request.Messages, providerId);
        if (historicalContainer is null)
            return metadata;

        metadata ??= [];
        var scopedMetadata = metadata.TryGetValue(providerId, out var existingProviderMetadata)
            && existingProviderMetadata.ValueKind == JsonValueKind.Object
                ? existingProviderMetadata.EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value.Clone())
                : [];

        scopedMetadata["container"] = historicalContainer.Value.Clone();
        metadata[providerId] = JsonSerializer.SerializeToElement(scopedMetadata, JsonSerializerOptions.Web);
        return metadata;
    }

    private static JsonElement? FindHistoricalAssistantContainer(
        IEnumerable<UIMessage>? messages,
        string providerId)
    {
        foreach (var message in messages?.Reverse() ?? [])
        {
            if (message.Role != Role.assistant || message.Metadata is null)
                continue;

            var metadata = JsonSerializer.SerializeToElement(message.Metadata, JsonSerializerOptions.Web);
            if (!TryGetMeaningfulProperty(metadata, "providerMetadata", out var providerMetadata)
                || !TryGetMeaningfulProperty(providerMetadata, providerId, out var scopedMetadata)
                || !TryGetMeaningfulProperty(scopedMetadata, "container", out var container)
                || container.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            return container.Clone();
        }

        return null;
    }

    private static bool TryGetMeaningfulProperty(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out value)
            && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
    }


    private static AIToolDefinition ToUnifiedTool(this Tool tool)
        => new()
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
            Title = tool.Title,
            AllowedCallers = tool.AllowedCallers,
            DeferLoading = tool.DeferLoading
        };

    public  static T? TryDeserialize<T>(this object? value)
    {
        if (value is null)
            return default;

        if (value is T cast)
            return cast;

        try
        {
            if (value is JsonElement json)
                return JsonSerializer.Deserialize<T>(json.GetRawText(), JsonSerializerOptions.Web);

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), JsonSerializerOptions.Web);
        }
        catch
        {
            return default;
        }
    }

}



