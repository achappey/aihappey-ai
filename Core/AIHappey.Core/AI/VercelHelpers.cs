using AIHappey.Common.Model;

namespace AIHappey.Core.AI;

public static class VercelHelpers
{
    public static string? GetReasoningSignature(this Dictionary<string, object>? keyValuePairs, string providerId)
      => keyValuePairs?.TryGetValue(providerId, out var providerObj) == true &&
                             providerObj is System.Text.Json.JsonElement providerJson &&
                             providerJson.ValueKind == System.Text.Json.JsonValueKind.Object &&
                             providerJson.TryGetProperty("signature", out var signatureProp)
                             ? signatureProp.GetString()
                             : null;


    public static TextStartUIMessageStreamPart ToTextStartUIMessageStreamPart(this string id)
        => new()
        {
            Id = id
        };

    public static TextEndUIMessageStreamPart ToTextEndUIMessageStreamPart(this string id)
        => new()
        {
            Id = id
        };

    public static ErrorUIPart ToErrorUIPart(this string error)
        => new()
        {
            ErrorText = error
        };


}
