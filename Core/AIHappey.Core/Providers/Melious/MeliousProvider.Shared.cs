using System.Globalization;
using System.Text.Json;

namespace AIHappey.Core.Providers.Melious;

public partial class MeliousProvider
{
    private static void MergeMeliousProviderOptions(Dictionary<string, object?> payload, JsonElement providerOptions)
    {
        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private static void AddMeliousProviderOptionsToForm(
        MultipartFormDataContent form,
        JsonElement providerOptions,
        ISet<string> excludedFields)
    {
        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
        {
            if (excludedFields.Contains(property.Name))
                continue;

            if (TryConvertMeliousFormScalar(property.Value, out var scalar))
                form.Add(new StringContent(scalar), property.Name);
        }
    }

    private static bool TryConvertMeliousFormScalar(JsonElement value, out string scalar)
    {
        scalar = string.Empty;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                scalar = value.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
                scalar = value.GetRawText();
                return true;
            case JsonValueKind.True:
                scalar = "true";
                return true;
            case JsonValueKind.False:
                scalar = "false";
                return true;
            default:
                return false;
        }
    }

    private static string? ReadMeliousStringProperty(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static float? ReadMeliousFloatProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number)
            return (float)property.GetDouble();

        if (property.ValueKind == JsonValueKind.String
            && float.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static decimal? ReadMeliousDecimalProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number)
            return property.GetDecimal();

        if (property.ValueKind == JsonValueKind.String
            && decimal.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
