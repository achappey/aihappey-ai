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

        var inputItems = request.Messages?.Select(a => a.ToUnifiedInputItem()).ToList() ?? [];

        return new AIRequest
        {
            ProviderId = providerId,
            Model = request.Model,
            Id = request.Id,
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
            Metadata = request.ProviderMetadata?
                .ToDictionary(p => p.Key, p => (object?)p.Value)
        };
    }


    private static AIToolDefinition ToUnifiedTool(this Tool tool)
        => new()
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
            Title = tool.Title
        };


}



