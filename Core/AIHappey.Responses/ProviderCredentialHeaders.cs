namespace AIHappey.Responses;

/// <summary>Separates HeaderAuth provider keys from endpoint/native authentication.</summary>
public static class ProviderCredentialHeaders
{
    public static bool IsProviderKey(string name) =>
        name.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith("-Key", StringComparison.OrdinalIgnoreCase)
        && !name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase)
        && !name.Equals("X-Goog-Api-Key", StringComparison.OrdinalIgnoreCase);

    public static bool IsRequired(string name, string? modelId)
    {
        if (!IsProviderKey(name)) return true;
        if (string.IsNullOrWhiteSpace(modelId) || !modelId.Contains('/')) return false;
        var provider = modelId.Split('/')[0].Trim().ToLowerInvariant() switch
        {
            "ai302" => "302ai",
            var value => value
        };
        return Normalize(name[2..^4]) == Normalize(provider);
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static Dictionary<string, string> ForModel(
        IEnumerable<KeyValuePair<string, string?>>? headers, string? modelId) =>
        (headers ?? []).Where(pair => !string.IsNullOrWhiteSpace(pair.Value) && IsRequired(pair.Key, modelId))
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
}
