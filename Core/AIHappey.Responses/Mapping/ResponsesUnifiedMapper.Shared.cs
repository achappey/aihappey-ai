using System.Text.Json;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static object? ParseJsonString(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch
        {
            return json;
        }
    }

    private static string SerializePayload(object? value, string fallback)
    {
        return value switch
        {
            null => fallback,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString() ?? fallback,
            JsonElement json => json.GetRawText(),
            string text => text,
            _ => JsonSerializer.Serialize(value, Json)
        };
    }

    private static T? ExtractObject<T>(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return default;

        if (value is T cast)
            return cast;

        try
        {
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json);
        }
        catch
        {
            return default;
        }
    }

    private static T? ExtractValue<T>(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return default;

        if (value is T cast)
            return cast;

        try
        {
            if (value is JsonElement json)
                return JsonSerializer.Deserialize<T>(json.GetRawText(), Json);

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json);
        }
        catch
        {
            return default;
        }
    }

    private static TEnum? ExtractEnum<TEnum>(Dictionary<string, object?>? metadata, string key)
        where TEnum : struct
    {
        var raw = ExtractValue<string>(metadata, key);
        if (string.IsNullOrWhiteSpace(raw))
            return default;

        if (Enum.TryParse<TEnum>(raw, true, out var parsed))
            return parsed;

        return default;
    }

    private static Dictionary<string, object?> ToJsonMap(object? value)
    {
        if (value is null)
            return new Dictionary<string, object?>();

        if (value is Dictionary<string, object?> dict)
            return dict;

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            return element.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(value, Json), Json)
                   ?? new Dictionary<string, object?>();
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    private static T GetValue<T>(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
            return default!;

        if (value is T cast)
            return cast;

        try
        {
            if (value is JsonElement json)
                return JsonSerializer.Deserialize<T>(json.GetRawText(), Json)!;

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
        }
        catch
        {
            return default!;
        }
    }

    private static TruncationStrategy? ParseTruncation(Dictionary<string, object?>? metadata, string key)
    {
        var raw = ExtractValue<string>(metadata, key);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.Trim().ToLowerInvariant() switch
        {
            "auto" => TruncationStrategy.Auto,
            "disabled" => TruncationStrategy.Disabled,
            _ => null
        };
    }
}
