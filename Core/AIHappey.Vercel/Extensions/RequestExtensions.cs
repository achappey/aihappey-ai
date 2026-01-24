using System.Text.Json;
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

}



