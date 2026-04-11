using System.Text.Json;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static bool TryGetUnknownEventProperty(ResponseUnknownEvent unknown, string key, out JsonElement value)
    {
        if (unknown.Data is not null)
        {
            foreach (var property in unknown.Data)
            {
                if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static object? GetAdditionalPropertyValue(Dictionary<string, JsonElement>? properties, string key)
    {
        if (properties?.TryGetValue(key, out var value) != true)
            return null;

        return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : value.Clone();
    }

    private static string? TryGetUnknownEventString(ResponseUnknownEvent unknown, string key)
        => TryGetUnknownEventProperty(unknown, key, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static int? TryGetAnnotationInt(ResponseStreamAnnotation annotation, string key)
    {
        if (annotation.AdditionalProperties?.TryGetValue(key, out var value) != true)
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;

        return null;
    }

    private static string? TryGetAnnotationString(ResponseStreamAnnotation annotation, string key)
    {
        if (annotation.AdditionalProperties?.TryGetValue(key, out var value) != true)
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static bool TryGetInt32(JsonElement value, out int number)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt32(out number);

        if (value.ValueKind == JsonValueKind.String)
            return int.TryParse(value.GetString(), out number);

        number = default;
        return false;
    }

    private static Dictionary<string, Dictionary<string, object>>? CreateProviderMetadata(
        string providerId,
        Dictionary<string, object?> providerMetadata)
    {
        var values = providerMetadata
            .Where(static entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value!);

        return values.Count == 0
            ? null
            : new Dictionary<string, Dictionary<string, object>>
            {
                [providerId] = values
            };
    }

    private static ResponseResult GetResponseResult(Dictionary<string, object?> data, AIEventEnvelope envelope)
    {
        if (data.TryGetValue("response", out var responseObj) && responseObj is not null)
        {
            try
            {
                return responseObj is ResponseResult existing
                    ? existing
                    : JsonSerializer.Deserialize<ResponseResult>(JsonSerializer.Serialize(responseObj, Json), Json)
                      ?? new ResponseResult { Id = Guid.NewGuid().ToString("N"), Model = "unknown" };
            }
            catch
            {
                // ignored
            }
        }

        return new ResponseResult
        {
            Id = Guid.NewGuid().ToString("N"),
            Object = "response",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = ExtractValue<string>(envelope.Metadata, "status"),
            Model = ExtractValue<string>(envelope.Metadata, "model") ?? "unknown",
            Output = []
        };
    }

    private static ResponseStreamItem GetResponseStreamItem(Dictionary<string, object?> data)
    {
        if (data.TryGetValue("item", out var itemObj) && itemObj is not null)
        {
            try
            {
                return itemObj is ResponseStreamItem item
                    ? item
                    : JsonSerializer.Deserialize<ResponseStreamItem>(JsonSerializer.Serialize(itemObj, Json), Json)
                      ?? new ResponseStreamItem { Type = "message" };
            }
            catch
            {
                // ignored
            }
        }

        return new ResponseStreamItem { Type = "message" };
    }

    private static ResponseStreamContentPart GetResponseStreamContentPart(Dictionary<string, object?> data, string key)
    {
        if (data.TryGetValue(key, out var partObj) && partObj is not null)
        {
            try
            {
                return partObj is ResponseStreamContentPart part
                    ? part
                    : JsonSerializer.Deserialize<ResponseStreamContentPart>(JsonSerializer.Serialize(partObj, Json), Json)
                      ?? new ResponseStreamContentPart { Type = "output_text" };
            }
            catch
            {
                // ignored
            }
        }

        return new ResponseStreamContentPart { Type = "output_text" };
    }

    private static ResponseStreamAnnotation GetResponseStreamAnnotation(Dictionary<string, object?> data)
    {
        if (data.TryGetValue("annotation", out var annotationObj) && annotationObj is not null)
        {
            try
            {
                return annotationObj is ResponseStreamAnnotation annotation
                    ? annotation
                    : JsonSerializer.Deserialize<ResponseStreamAnnotation>(JsonSerializer.Serialize(annotationObj, Json), Json)
                      ?? new ResponseStreamAnnotation();
            }
            catch
            {
                // ignored
            }
        }

        return new ResponseStreamAnnotation();
    }

    private static Dictionary<string, JsonElement>? ToJsonElementMap(object? value)
    {
        if (value is null)
            return null;

        try
        {
            if (value is Dictionary<string, JsonElement> already)
                return already;

            if (value is JsonElement json && json.ValueKind == JsonValueKind.Object)
            {
                return json.EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value);
            }

            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value, Json), Json);
        }
        catch
        {
            return null;
        }
    }
}
