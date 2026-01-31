using System.Text.Json;
using Anthropic.SDK.Common;
using Anthropic.SDK.Messaging;
using ANT = Anthropic.SDK;

namespace AIHappey.Core.Providers.Anthropic.Extensions;

public static partial class AnthropicExtensions
{

    public static Dictionary<string, object> ToProviderMetadata(this Dictionary<string, object> metadata)
          => new()
          { { AnthropicConstants.AnthropicIdentifier, metadata } };

    public static ThinkingParameters? ToThinkingConfig(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(AnthropicConstants.AnthropicIdentifier, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("thinking", out var reasoning) || reasoning.ValueKind != JsonValueKind.Object)
            return null;

        var size = reasoning.TryGetProperty("budget_tokens", out var effortProp) && effortProp.ValueKind == JsonValueKind.Number
            ? (int?)effortProp.GetInt32()
            : null;

        return new ThinkingParameters()
        {
            BudgetTokens = size ?? 4096
        };
    }


    public static Container? ToContainer(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(AnthropicConstants.AnthropicIdentifier, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("container", out var containerEl) || containerEl.ValueKind != JsonValueKind.Object)
            return null;

        var id = containerEl.TryGetProperty("id", out var effortProp) && effortProp.ValueKind == JsonValueKind.String
            ? effortProp.ToString() : null;

        // Extract skills (if present)
        List<Skill>? skills = null;
        if (containerEl.TryGetProperty("skills", out var skillsEl) && skillsEl.ValueKind == JsonValueKind.Array)
        {
            skills = new List<Skill>();
            foreach (var skillEl in skillsEl.EnumerateArray())
            {
                if (skillEl.ValueKind != JsonValueKind.Object) continue;

                var type = skillEl.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                    ? typeProp.GetString()
                    : null;

                var skillId = skillEl.TryGetProperty("skill_id", out var idProp2) && idProp2.ValueKind == JsonValueKind.String
                    ? idProp2.GetString()
                    : null;

                var version = skillEl.TryGetProperty("version", out var verProp) && verProp.ValueKind == JsonValueKind.String
                    ? verProp.GetString()
                    : null;

                if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(skillId))
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

        // Construct and return container
        return new Container
        {
            Id = id,
            Skills = skills?.Count > 0 ? skills : []
        };
    }

    public static IReadOnlyList<string>? ToAnthropicBetaFeatures(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(AnthropicConstants.AnthropicIdentifier, out var openai) ||
            openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("anthropic-beta", out var beta) ||
            beta.ValueKind != JsonValueKind.Array)
            return null;

        var features = new List<string>();

        foreach (var item in beta.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                features.Add(item.GetString()!);
        }

        return features.Count > 0 ? features : null;
    }


    public static ANT.Common.Tool? ToWebSearchTool(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(AnthropicConstants.AnthropicIdentifier, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("web_search", out var reasoning) || reasoning.ValueKind != JsonValueKind.Object)
            return null;

        var size = reasoning.TryGetProperty("max_uses", out var effortProp) && effortProp.ValueKind == JsonValueKind.Number
            ? (int?)effortProp.GetInt32()
            : null;

        return ServerTools.GetWebSearchTool(maxUses: size ?? 5);
    }

    public static ANT.Common.Tool? ToCodeExecution(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(AnthropicConstants.AnthropicIdentifier, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("code_execution", out var reasoning) || reasoning.ValueKind != JsonValueKind.Object)
            return null;

        return new Function("code_execution", "code_execution_20250825", []);
    }
}
