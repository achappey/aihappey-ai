using System.Text.Json;

namespace AIHappey.Core.Providers.CallMissed;

public partial class CallMissedProvider
{
    private static void MergeProviderOptions(Dictionary<string, object?> payload, JsonElement providerOptions)
    {
        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private static string? ReadStringProperty(JsonElement el, string propertyName)
    {
        return el.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static long? ReadLongProperty(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt64();

        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), out var parsed))
            return parsed;

        return null;
    }
}
