namespace AIHappey.Core.Extensions;

public static class ProviderHeaderPassthroughExtensions
{
    public static Dictionary<string, string> GetProviderPassthroughHeaders(
        this IEnumerable<KeyValuePair<string, string?>> headers,
        string? providerKey)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (headers is null || string.IsNullOrWhiteSpace(providerKey))
            return result;

        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                continue;

            if (!IsProviderPassthroughHeader(name, providerKey))
                continue;

            result[name] = value;
        }

        return result;
    }

    public static bool IsProviderPassthroughHeader(string? headerName, string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(headerName) || string.IsNullOrWhiteSpace(providerKey))
            return false;

        if (headerName.Equals("HTTP-Referer", StringComparison.OrdinalIgnoreCase) || headerName.Equals("X-Title", StringComparison.OrdinalIgnoreCase))
            return true;

        var providerPrefix = providerKey + "-";
        var providerXPrefix = "x-" + providerKey + "-";

        if (headerName.StartsWith(providerXPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (headerName.Length == providerXPrefix.Length)
                return false;

            return !string.Equals(
                headerName,
                "x-" + providerKey + "-key",
                StringComparison.OrdinalIgnoreCase);
        }

        return headerName.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
               && headerName.Length > providerPrefix.Length;
    }
}
