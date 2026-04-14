using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.Mistral;

public static partial class MistralExtensions
{

    public static double? GetDouble(
    this Dictionary<string, object>? dict,
    string key)
    {
        if (dict is null || !dict.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,

            string s when double.TryParse(s, out var d) => d,

            JsonElement j when j.ValueKind == JsonValueKind.Number && j.TryGetDouble(out var d) => d,

            _ => null
        };
    }

    public static bool TryGetExplicitToolNodes(this JsonObject? obj, out List<JsonNode> tools)
    {
        tools = [];

        if (obj?["mistral"] is not JsonObject mistral || mistral["tools"] is not JsonArray toolsArray)
            return false;

        foreach (var tool in toolsArray)
        {
            if (TryCloneToolNode(tool) is { } node)
                tools.Add(node);
        }

        return true;
    }

    private static JsonNode? TryCloneToolNode(JsonNode? tool)
    {
        if (tool is not JsonObject toolObject)
            return null;

        if (toolObject["type"] is not JsonValue typeValue
            || !typeValue.TryGetValue<string>(out var type)
            || string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(toolObject.ToJsonString());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
