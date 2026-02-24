using System.Text.Json.Nodes;
using Anthropic.SDK.Common;
using Anthropic.SDK.Messaging;
using ANT = Anthropic.SDK;

namespace AIHappey.Core.Providers.Anthropic.Extensions;

public static partial class AnthropicExtensions
{

    public static Dictionary<string, object> ToProviderMetadata(this Dictionary<string, object> metadata)
          => new()
          { { AnthropicConstants.AnthropicIdentifier, metadata } };

    public static ThinkingParameters? ToThinkingConfig(this JsonObject? obj)
    {
        var thinking = GetAnthropicTool(obj, "thinking");
        if (thinking is null)
            return null;

        var budget = thinking["budget_tokens"] is JsonValue v &&
                     v.TryGetValue<int>(out var parsed)
            ? parsed
            : 4096;

        return new ThinkingParameters
        {
            BudgetTokens = budget
        };
    }

    public static Container? ToContainer(this JsonObject? obj)
    {
        var containerObj = GetAnthropicTool(obj, "container");
        if (containerObj is null)
            return null;

        var id = containerObj["id"] is JsonValue idVal &&
                 idVal.TryGetValue<string>(out var parsedId)
            ? parsedId
            : null;

        List<Skill> skills = [];

        if (containerObj["skills"] is JsonArray skillsArray)
        {
            foreach (var node in skillsArray.OfType<JsonObject>())
            {
                var type = node["type"] is JsonValue t && t.TryGetValue<string>(out var tVal)
                    ? tVal
                    : null;

                var skillId = node["skill_id"] is JsonValue s && s.TryGetValue<string>(out var sVal)
                    ? sVal
                    : null;

                var version = node["version"] is JsonValue v && v.TryGetValue<string>(out var vVal)
                    ? vVal
                    : null;

                if (!string.IsNullOrWhiteSpace(type) &&
                    !string.IsNullOrWhiteSpace(skillId))
                {
                    skills.Add(new Skill
                    {
                        Type = type!,
                        SkillId = skillId!,
                        Version = version
                    });
                }
            }
        }

        return new Container
        {
            Id = id,
            Skills = skills
        };
    }

    private static JsonObject? GetAnthropicTool(JsonObject? root, string tool)
    {
        if (root?[AnthropicConstants.AnthropicIdentifier] is not JsonObject a)
            return null;

        return a[tool] as JsonObject;
    }


    public static IReadOnlyList<string>? ToAnthropicBetaFeatures(this JsonObject? obj)
    {
        if (obj is null)
            return null;

        if (obj[AnthropicConstants.AnthropicIdentifier] is not JsonObject anthropic)
            return null;

        if (anthropic["anthropic-beta"] is not JsonArray betaArray)
            return null;

        var features = new List<string>();

        foreach (var node in betaArray)
        {
            if (node is JsonValue value &&
                value.TryGetValue<string>(out var str) &&
                !string.IsNullOrWhiteSpace(str))
            {
                features.Add(str);
            }
        }

        return features.Count > 0 ? features : null;
    }

    public static ANT.Common.Tool? ToWebSearchTool(this JsonObject? obj)
    {
        var webSearch = GetAnthropicTool(obj, "web_search");
        if (webSearch is null)
            return null;

        var maxUses = webSearch["max_uses"] is JsonValue v &&
                      v.TryGetValue<int>(out var parsed)
            ? parsed
            : 5;

        return ServerTools.GetWebSearchTool(maxUses: maxUses);
    }
    public static ANT.Common.Tool? ToCodeExecution(this JsonObject? obj)
    {
        return GetAnthropicTool(obj, "code_execution") is not null
            ? new Function("code_execution", "code_execution_20250825", [])
            : null;
    }
}
