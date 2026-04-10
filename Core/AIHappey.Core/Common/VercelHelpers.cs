using System.Globalization;
using System.Text.Json;

namespace AIHappey.Core.AI;

public static class VercelHelpers
{
    public static decimal NormalizeTokenPrice(this string price)
    {
        decimal inputPerToken = decimal.Parse(price, CultureInfo.InvariantCulture);
        return inputPerToken * 1_000_000m;
    }

    public static string? GetReasoningSignature(
     this Dictionary<string, object>? keyValuePairs,
     string providerId)
    {
        if (keyValuePairs?.TryGetValue(providerId, out var providerObj) != true
            || !TryGetProviderJson(providerObj, out var providerJson))
        {
            return null;
        }

        return (
            providerJson.TryGetProperty("encrypted_content", out var signatureProp) ||
            providerJson.TryGetProperty("signature", out signatureProp)
        )
            ? signatureProp.ValueKind == JsonValueKind.String
                ? signatureProp.GetString()
                : signatureProp.ToString()
            : null;
    }

    public static T? GetProviderProperty<T>(
        this Dictionary<string, object>? keyValuePairs,
        string providerId,
        string propertyName)
    {
        if (keyValuePairs?.TryGetValue(providerId, out var providerObj) != true
            || !TryGetProviderJson(providerObj, out var providerJson))
            return default;

        if (!providerJson.TryGetProperty(propertyName, out var prop))
            return default;

        return prop.Deserialize<T>();
    }

    public static T? GetProviderMetadata<T>(
        this Dictionary<string, object?>? keyValuePairs,
        string providerId)
    {
        if (keyValuePairs?.TryGetValue(providerId, out var providerObj) != true
            || !TryGetProviderJson(providerObj, out var providerJson))
            return default;

        return providerJson.Deserialize<T>();
    }

    private static bool TryGetProviderJson(object? providerObj, out JsonElement providerJson)
    {
        switch (providerObj)
        {
            case JsonElement json when json.ValueKind == JsonValueKind.Object:
                providerJson = json;
                return true;
            case Dictionary<string, object> dict:
                providerJson = JsonSerializer.SerializeToElement(dict, JsonSerializerOptions.Web);
                return true;
//            case Dictionary<string, object?> nullableDict:
 //               providerJson = JsonSerializer.SerializeToElement(nullableDict, JsonSerializerOptions.Web);
  //              return true;
            case null:
                providerJson = default;
                return false;
            default:
                try
                {
                    providerJson = JsonSerializer.SerializeToElement(providerObj, JsonSerializerOptions.Web);
                    return providerJson.ValueKind == JsonValueKind.Object;
                }
                catch
                {
                    providerJson = default;
                    return false;
                }
        }
    }



}
