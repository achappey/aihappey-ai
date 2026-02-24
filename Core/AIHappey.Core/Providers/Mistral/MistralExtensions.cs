using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model.Providers.Mistral;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

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

    public static Dictionary<string, object> ToProviderMetadata(this Dictionary<string, object> metadata)
        => new()
        { { "mistral", metadata } };

    public static object? ToMistralPart(this UIMessagePart part)
    {
        if (part is TextUIPart t) return t.ToTextPart();

        if (part is FileUIPart f && f.IsImage())
            return f.ToImagePart();

        return null;
    }

    public static object? ToTextPart(this TextUIPart part) =>
        new { type = "text", text = part.Text };

    public static object? ToImagePart(this FileUIPart part) =>
        new { type = "image_url", image_url = part.Url };

    public static IEnumerable<object> ToMistralMessages(this UIMessage msg)
    {
        //──────────────────────────────────────────────
        // USER / SYSTEM
        //──────────────────────────────────────────────
        if (msg.Role == Role.user || msg.Role == Role.system)
        {
            yield return new Dictionary<string, object?>
            {
                ["type"] = "message.input",
                ["role"] = msg.Role.ToString(),
                ["content"] = msg.Parts
                    .Select(p => p.ToMistralPart())
                    .OfType<object>()
                    .ToList()
            };
            yield break;
        }

        //──────────────────────────────────────────────
        // ASSISTANT (ordered per-part)
        //──────────────────────────────────────────────
        if (msg.Role == Role.assistant)
        {
            foreach (var part in msg.Parts)
            {
                switch (part)
                {
                    // 🧩 Plain text or image parts
                    case TextUIPart or FileUIPart:
                        yield return new Dictionary<string, object?>
                        {
                            ["type"] = "message.input",
                            ["role"] = "assistant",
                            ["content"] = new[] { part.ToMistralPart()! }
                        };
                        break;

                    // 🧩 Tool invocation part
                    case ToolInvocationPart tool:
                        var toolName = tool.GetToolName();

                        if (tool.ProviderExecuted != true)
                        {
                            // Assistant calls the tool
                            yield return new Dictionary<string, object?>
                            {
                                ["type"] = "function.call",
                                ["tool_call_id"] = tool.ToolCallId,
                                ["name"] = toolName,
                                ["arguments"] = JsonSerializer.Serialize(tool.Input)
                            };

                            // Optional tool output
                            if (tool.Output is not null)
                            {
                                yield return new Dictionary<string, object?>
                                {
                                    ["type"] = "function.result",
                                    ["tool_call_id"] = tool.ToolCallId,
                                    ["result"] = tool.Output is string s
                                        ? s
                                        : JsonSerializer.Serialize(tool.Output)
                                };
                            }
                        }

                        break;
                }
            }

            yield break;
        }

        throw new InvalidOperationException($"Unexpected role: {msg.Role}");
    }
}
