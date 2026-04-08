using System.Text.Json;

namespace AIHappey.Common.Extensions;

public static class JsonExtensions
{
    public static T? GetProviderOption<T>(
    this Dictionary<string, object?>? metadata,
    string providerId,
    string key)
    {
        if (metadata is null)
            return default;

        if (!metadata.TryGetValue(providerId, out var provider))
            return default;

        if (provider is null)
            return default;

        var element = provider switch
        {
            JsonElement je => je,
            _ => JsonSerializer.SerializeToElement(provider, JsonSerializerOptions.Web)
        };

        if (element.ValueKind != JsonValueKind.Object)
            return default;

        if (!element.TryGetProperty(key, out var value))
            return default;

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default;

        return value.Deserialize<T>(JsonSerializerOptions.Web);
    }

    public static object DeserializeToObject(this string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new { };

        try
        {
            return JsonSerializer.Deserialize<object>(json, JsonSerializerOptions.Web) ?? new { };
        }
        catch
        {
            return json;
        }
    }

    public static string? TryGetString(this Dictionary<string, JsonElement>? data, params string[] names)
    {
        if (data == null)
            return null;

        foreach (var name in names)
        {
            if (data.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    public static string? TryGetString(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    public static string? TryGetNestedString(this Dictionary<string, JsonElement>? data, string parentName, string childName)
    {
        if (data == null || !data.TryGetValue(parentName, out var parent) || parent.ValueKind != JsonValueKind.Object)
            return null;

        return TryGetString(parent, childName);
    }

    public static int? TryGetNumber(this Dictionary<string, JsonElement>? data, params string[] names)
    {
        if (data == null)
            return null;

        foreach (var name in names)
        {
            if (data.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
        }

        return null;
    }

    public static int? TryGetNumber(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
        }

        return null;
    }
}
