using System.Globalization;
using AIHappey.Common.Model;

namespace AIHappey.Core.AI;

public static class VercelHelpers
{
    public static decimal NormalizeTokenPrice(this string price)
    {
        decimal inputPerToken = decimal.Parse(price, CultureInfo.InvariantCulture);
        return inputPerToken * 1_000_000m;
    }

    public static string? GetReasoningSignature(this Dictionary<string, object>? keyValuePairs, string providerId)
      => keyValuePairs?.TryGetValue(providerId, out var providerObj) == true &&
                             providerObj is System.Text.Json.JsonElement providerJson &&
                             providerJson.ValueKind == System.Text.Json.JsonValueKind.Object &&
                             providerJson.TryGetProperty("signature", out var signatureProp)
                             ? signatureProp.GetString()
                             : null;




}
