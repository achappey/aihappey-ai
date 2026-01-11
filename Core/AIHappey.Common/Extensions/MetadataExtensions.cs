using System.Text.Json;
using AIHappey.Common.Model;

namespace AIHappey.Common.Extensions;

public static class MetadataExtensions
{

    public static ResponseFormat? GetJSONSchema(this object? structured)
    {
        if (structured == null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<ResponseFormat>(JsonSerializer.Serialize(structured));
        }
        catch
        {
            return null;
        }
    }

    public static T? GetTranscriptionProviderMetadata<T>(this TranscriptionRequest chatRequest, string providerId)
    {
        if (chatRequest.ProviderOptions is null)
            return default;

        if (!chatRequest.ProviderOptions.TryGetValue(providerId, out JsonElement element))
            return default;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }


    public static T? GetSpeechProviderMetadata<T>(this SpeechRequest chatRequest, string providerId)
    {
        if (chatRequest.ProviderOptions is null)
            return default;

        if (!chatRequest.ProviderOptions.TryGetValue(providerId, out JsonElement element))
            return default;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }


    public static T? GetImageProviderMetadata<T>(this ImageRequest chatRequest, string providerId)
    {
        if (chatRequest.ProviderOptions is null)
            return default;

        if (!chatRequest.ProviderOptions.TryGetValue(providerId, out JsonElement element))
            return default;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }

    public static T? GetProviderMetadata<T>(this RerankingRequest chatRequest, string providerId)
    {
        if (chatRequest.ProviderOptions is null)
            return default;

        if (!chatRequest.ProviderOptions.TryGetValue(providerId, out JsonElement element))
            return default;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }

    public static T? GetProviderMetadata<T>(this ChatRequest chatRequest, string providerId)
    {
        if (chatRequest.ProviderMetadata is null)
            return default;

        if (!chatRequest.ProviderMetadata.TryGetValue(providerId, out JsonElement element))
            return default;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }


    public static List<UIMessage> EnsureApprovals(this List<UIMessage> uIMessages) =>
   [.. uIMessages.Select(a =>
            {
                a.Parts = [.. a.Parts.Select(z =>
                {
                    if(z is ToolInvocationPart toolInvocationPart) {
                    if(toolInvocationPart.State == "approval-responded" && toolInvocationPart.Approval?.Approved == false)
                            {
                                toolInvocationPart.Output = toolInvocationPart.Approval;
                            }
                    }
                return z;
                })];

                return a;
            })];

}
