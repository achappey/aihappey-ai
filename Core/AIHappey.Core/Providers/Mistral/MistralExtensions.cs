using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model.Providers.Mistral;

namespace AIHappey.Core.Providers.Mistral;

public static partial class MistralExtensions
{

    private static bool HasMistralTool(JsonObject? root, string tool)
        => root?["mistral"] is JsonObject m && m[tool] is JsonObject;

    public static MistralWebSearch? ToWebSearchTool(this JsonObject? obj)
        => HasMistralTool(obj, "web_search") ? new MistralWebSearch() : null;

    public static MistralImageGeneration? ToImageGeneration(this JsonObject? obj)
        => HasMistralTool(obj, "image_generation") ? new MistralImageGeneration() : null;

    public static MistralWebSearchPremium? ToWebSearchPremiumTool(this JsonObject? obj)
        => HasMistralTool(obj, "web_search_premium") ? new MistralWebSearchPremium() : null;

    public static MistralCodeInterpreter? ToCodeInterpreter(this JsonObject? obj)
        => HasMistralTool(obj, "code_interpreter") ? new MistralCodeInterpreter() : null;

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

    public static Dictionary<string, object> ToProviderMetadata(this Dictionary<string, object> metadata)
        => new()
        { { "mistral", metadata } };

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
