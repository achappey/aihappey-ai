using System.Security.Claims;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Identity.Web;

namespace AIHappey.Common.Extensions;

public static class VercelExtensions
{


    public static T? GetProviderMetadata<T>(this ChatRequest chatRequest, string providerId)
    {
        if (chatRequest.ProviderMetadata is null)
            return default;

        if (!chatRequest.ProviderMetadata.ContainsKey(providerId))
            return default;

        var element = chatRequest.ProviderMetadata[providerId];

        //   var el = element.Value;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }


    public static T? GetProviderMetadata2222<T>(this JsonElement? element, JsonSerializerOptions? options = null)
    {
        if (element is null)
            return default;

        var el = element.Value;

        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            return default;

        return el.Deserialize<T>(options ?? JsonSerializerOptions.Web);
    }

    public static T? GetProviderMetadata2<T>(this object metadata)
    {
        if (metadata == null)
            return default;

        if (metadata is T item)
            return item;

        if (metadata is ChatRequest chatRequest && chatRequest.ProviderMetadata is T itemData)
            return itemData;

        return default;
    }
}
