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
     => keyValuePairs?.TryGetValue(providerId, out var providerObj) == true &&
        providerObj is JsonElement providerJson &&
        providerJson.ValueKind == JsonValueKind.Object &&
        (
            providerJson.TryGetProperty("encrypted_content", out var signatureProp) ||
            providerJson.TryGetProperty("signature", out signatureProp)
        )
         ? signatureProp.GetString()
         : null;

    public static T? GetProviderProperty<T>(
        this Dictionary<string, object>? keyValuePairs,
        string providerId,
        string propertyName)
    {
        if (keyValuePairs?.TryGetValue(providerId, out var providerObj) != true)
            return default;

        if (providerObj is not JsonElement providerJson ||
            providerJson.ValueKind != JsonValueKind.Object)
            return default;

        if (!providerJson.TryGetProperty(propertyName, out var prop))
            return default;

        return prop.Deserialize<T>();
    }

    public static T? GetProviderMetadata<T>(
        this Dictionary<string, object?>? keyValuePairs,
        string providerId)
    {
        if (keyValuePairs?.TryGetValue(providerId, out var providerObj) != true)
            return default;

        if (providerObj is not JsonElement providerJson ||
            providerJson.ValueKind != JsonValueKind.Object)
            return default;

        return providerJson.Deserialize<T>();
    }



}
