using System.Text.Json;

namespace AIHappey.Core.Providers.MumeAI;

public partial class MumeAIProvider
{
    private static Dictionary<string, object?> MumePayload(JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (metadata.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in metadata.EnumerateObject())
            payload[property.Name] = property.Value.Clone();

        return payload;
    }

    private static Dictionary<string, object?> MumePayload(Dictionary<string, JsonElement>? additionalProperties)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (additionalProperties is null)
            return payload;

        foreach (var property in additionalProperties)
            payload[property.Key] = property.Value.Clone();

        return payload;
    }

    private JsonElement GetMumeProviderOptions(Dictionary<string, JsonElement>? providerOptions)
        => providerOptions is not null
            && providerOptions.TryGetValue(GetIdentifier(), out var metadata)
                ? metadata
                : default;

    private static string? MumeString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static double? MumeNumber(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                return value.GetDouble();
        }

        return null;
    }
}
