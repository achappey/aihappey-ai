using System.Security.Claims;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Identity.Web;

namespace AIHappey.Common.Extensions;

public static class VercelExtensions
{
    public static string ToDataUrl(
        this string data, string mimeType) => $"data:{mimeType};base64,{data}";

    public static string ToDataUrl(this ImageFile imageContentBlock) => imageContentBlock.Data.ToDataUrl(imageContentBlock.MediaType);

    public static int? GetImageWidth(this ImageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Size))
            return null;

        var parts = request.Size.Split('x', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        return int.TryParse(parts[0], out var width) ? width : null;
    }

    public static int? GetImageHeight(this ImageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Size))
            return null;

        var parts = request.Size.Split('x', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        return int.TryParse(parts[1], out var width) ? width : null;
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

}
