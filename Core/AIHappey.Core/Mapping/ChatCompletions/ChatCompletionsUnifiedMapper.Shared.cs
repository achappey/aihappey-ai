using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.ChatCompletions.Mapping;

public static partial class ChatCompletionsUnifiedMapper
{
    public static void ApplyProviderOptions(
   this string provider,
   Dictionary<string, object?>? metadata,
   IDictionary<string, JsonElement>? additional, HashSet<string>? exclude = null)
    {
        if (metadata is null || additional is null)
            return;

        if (!metadata.TryGetValue(provider, out var obj))
            return;

        if (obj is not JsonElement json)
            return;

        foreach (var prop in json.EnumerateObject())
        {
            if (exclude?.Contains(prop.Name) == true)
                continue;

            additional[prop.Name] = prop.Value;
        }
    }

    public static List<object>? GetChatCompletionToolDefinitions(
     this Dictionary<string, object?>? metadata,
     string providerId)
    {
        if (metadata is null)
            return null;

        if (!metadata.TryGetValue(providerId, out var providerObj) || providerObj is null)
            return null;

        var toolsEl = ExtractToolsArray(providerObj);
        if (toolsEl is null || toolsEl.Value.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<object>();

        foreach (var toolEl in toolsEl.Value.EnumerateArray())
        {
            try
            {
                // raw passthrough object
                var obj = JsonSerializer.Deserialize<object>(toolEl.GetRawText(), JsonSerializerOptions.Web);
                if (obj != null)
                    result.Add(obj);
            }
            catch
            {
                // ignore bad entries
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static JsonElement ToJsonElement(object? obj)
    {
        if (obj is JsonElement je)
            return je;

        if (obj is JsonNode node)
            return JsonSerializer.SerializeToElement(node, JsonSerializerOptions.Web);

        return JsonSerializer.SerializeToElement(obj, JsonSerializerOptions.Web);
    }


    private static JsonElement? ExtractToolsArray(object providerObj)
    {
        switch (providerObj)
        {
            case JsonElement je:
                if (je.ValueKind == JsonValueKind.Object &&
                    je.TryGetProperty("tools", out var t) &&
                    t.ValueKind == JsonValueKind.Array)
                    return t;
                break;

            case JsonObject jo:
                if (jo["tools"] is JsonArray arr)
                    return ToJsonElement(arr);
                break;

            case Dictionary<string, object?> dict:
                if (dict.TryGetValue("tools", out var toolsObj))
                    return ToJsonElement(toolsObj);
                break;
        }

        // 🔥 last resort: serialize → parse → extract
        try
        {
            var json = JsonSerializer.Serialize(providerObj, JsonSerializerOptions.Web);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("tools", out var t) &&
                t.ValueKind == JsonValueKind.Array)
            {
                return t.Clone(); // important: avoid disposed doc
            }
        }
        catch { }

        return null;
    }

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

    private static Dictionary<string, JsonElement>? BuildChatCompletionRequestAdditionalProperties(AIRequest request)
    {
        var additional = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        if (request.MaxOutputTokens is int maxTokens)
            additional["max_tokens"] = JsonSerializer.SerializeToElement(maxTokens, Json);

        return additional.Count > 0 ? additional : null;
    }

    private static string? NormalizeRequestModel(string? model, string? providerId)
    {
        var modelText = model?.Trim();
        if (string.IsNullOrWhiteSpace(modelText))
            return modelText;

        if (string.IsNullOrWhiteSpace(providerId))
            return modelText;

        var prefix = providerId.Trim() + "/";
        return modelText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? modelText[prefix.Length..]
            : modelText;
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
