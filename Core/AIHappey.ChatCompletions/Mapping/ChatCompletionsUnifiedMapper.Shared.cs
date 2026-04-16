using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.ChatCompletions.Mapping;

public static partial class ChatCompletionsUnifiedMapper
{
    private static readonly HashSet<string> MappedRequestFields =
    [
        "model",
        "temperature",
        "top_p",
        "max_completion_tokens",
        "max_tokens",
        "stream",
        "parallel_tool_calls",
        "tool_choice",
        "response_format",
        "tools",
        "messages",
        "store"
    ];

    private static readonly HashSet<string> KnownChatCompletionResponseFields =
    [
        "id",
        "object",
        "created",
        "model",
        "choices",
        "usage"
    ];

    private static readonly HashSet<string> KnownChatCompletionStreamFields =
    [
        "id",
        "object",
        "created",
        "model",
        "service_tier",
        "choices",
        "usage"
    ];

    private static T? ExtractValue<T>(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(propertyName, out var value))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(value.GetRawText(), Json);
        }
        catch
        {
            return default;
        }
    }

    private static string? TryGetString(object? value)
    {
        if (value is null)
            return null;

        if (value is string s)
            return s;

        if (value is JsonElement j && j.ValueKind == JsonValueKind.String)
            return j.GetString();

        return null;
    }

    private static string? NormalizeToolChoice(object? toolChoice, List<AIToolDefinition>? tools)
    {
        var value = TryGetString(toolChoice)?.Trim();

        if (string.IsNullOrWhiteSpace(value))
            return tools is { Count: > 0 } ? "auto" : "none";

        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return "none";

        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return "auto";

        if (value.Equals("required", StringComparison.OrdinalIgnoreCase))
            return "required";

        return tools is { Count: > 0 } ? "auto" : "none";
    }

    private static JsonElement? ExtractMetadataElement(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is JsonElement element)
            return element;

        try
        {
            return JsonSerializer.SerializeToElement(value, Json);
        }
        catch
        {
            return null;
        }
    }

    private static T? ExtractMetadataValue<T>(Dictionary<string, object?>? metadata, string key)
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

    private static List<object> ExtractMetadataEnumerable(Dictionary<string, object?>? metadata, string key)
    {
        var value = ExtractMetadataElement(metadata, key);
        if (value is null || value.Value.ValueKind != JsonValueKind.Array)
            return [];

        return [.. value.Value.EnumerateArray().Select(a => (object)a.Clone())];
    }

    private static List<object> ExtractEnumerable(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        return [.. arr.EnumerateArray().Select(a => (object)a.Clone())];
    }

    private static void Set<T>(JsonObject obj, string name, T? value)
    {
        if (value is null)
            return;

        obj[name] = JsonValue.Create(value);
    }

    private static JsonNode? ToJsonNode(object value)
    {
        if (value is JsonElement element)
            return JsonNode.Parse(element.GetRawText());

        return JsonSerializer.SerializeToNode(value, Json);
    }

    private static Dictionary<string, JsonElement>? BuildChatCompletionAdditionalProperties(
        Dictionary<string, object?>? metadata)
    {
        var additional = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var known = KnownChatCompletionResponseFields;

        var raw = ExtractMetadataElement(metadata, "chatcompletions.response.raw");
        if (raw is { ValueKind: JsonValueKind.Object })
        {
            foreach (var prop in raw.Value.EnumerateObject())
            {
                if (!known.Contains(prop.Name))
                    additional[prop.Name] = prop.Value.Clone();
            }
        }

        return additional.Count > 0 ? additional : null;
    }

    private static Dictionary<string, JsonElement>? BuildChatCompletionUpdateAdditionalProperties(
        Dictionary<string, object?>? metadata)
    {
        var additional = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var known = KnownChatCompletionStreamFields;

        var raw = ExtractMetadataElement(metadata, "chatcompletions.stream.raw");
        if (raw is { ValueKind: JsonValueKind.Object })
        {
            foreach (var prop in raw.Value.EnumerateObject())
            {
                if (!known.Contains(prop.Name))
                    additional[prop.Name] = prop.Value.Clone();
            }
        }

        return additional.Count > 0 ? additional : null;
    }

    private static Dictionary<string, JsonElement>? ExtractAdditionalProperties(JsonElement obj, HashSet<string> knownFields)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        var additional = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in obj.EnumerateObject())
        {
            if (!knownFields.Contains(prop.Name))
                additional[prop.Name] = prop.Value.Clone();
        }

        return additional.Count > 0 ? additional : null;
    }
}
