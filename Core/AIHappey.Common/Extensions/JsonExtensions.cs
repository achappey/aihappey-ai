using System.Text.Json;

namespace AIHappey.Common.Extensions;

public static class JsonExtensions
{
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
