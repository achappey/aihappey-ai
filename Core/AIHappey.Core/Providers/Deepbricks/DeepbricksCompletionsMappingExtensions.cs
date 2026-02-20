using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Deepbricks;

/// <summary>
/// Deepbricks-compatible message/tool mapping.
/// Contract enforced: messages[].content is always a string.
/// Non-text UI parts are silently dropped.
/// </summary>
public static class DeepbricksCompletionsMappingExtensions
{
    private static readonly JsonSerializerOptions J = JsonSerializerOptions.Web;

    public static IEnumerable<object> ToDeepbricksMessages(this IEnumerable<UIMessage> uiMessages)
    {
        foreach (var msg in uiMessages)
        {
            switch (msg.Role)
            {
                case Role.system:
                case Role.user:
                    {
                        var role = msg.Role == Role.system ? "system" : "user";
                        var content = string.Join("\n", msg.Parts.OfType<TextUIPart>().Select(a => a.Text).Where(a => !string.IsNullOrWhiteSpace(a)));

                        if (!string.IsNullOrWhiteSpace(content))
                            yield return new { role, content };

                        break;
                    }

                case Role.assistant:
                    {
                        var buffer = new List<string>();

                        foreach (var part in msg.Parts)
                        {
                            switch (part)
                            {
                                case TextUIPart t when !string.IsNullOrWhiteSpace(t.Text):
                                    buffer.Add(t.Text);
                                    break;

                                case ToolInvocationPart tip:
                                    {
                                        if (buffer.Count > 0)
                                        {
                                            yield return new
                                            {
                                                role = "assistant",
                                                content = string.Join("\n", buffer)
                                            };
                                            buffer.Clear();
                                        }

                                        var toolName = tip.GetToolName();
                                        var argsJson = tip.Input is null ? "{}" : JsonSerializer.Serialize(tip.Input, J);

                                        yield return new
                                        {
                                            role = "assistant",
                                            content = string.Empty,
                                            tool_calls = new[]
                                            {
                                                new
                                                {
                                                    id = tip.ToolCallId,
                                                    type = "function",
                                                    function = new
                                                    {
                                                        name = toolName,
                                                        arguments = argsJson
                                                    }
                                                }
                                            }
                                        };

                                        if (tip.Output is not null)
                                        {
                                            var toolContent = tip.Output is string s
                                                ? s
                                                : JsonSerializer.Serialize(tip.Output, J);

                                            yield return new
                                            {
                                                role = "tool",
                                                tool_call_id = tip.ToolCallId,
                                                name = toolName,
                                                content = toolContent
                                            };
                                        }

                                        break;
                                    }
                            }
                        }

                        if (buffer.Count > 0)
                        {
                            yield return new
                            {
                                role = "assistant",
                                content = string.Join("\n", buffer)
                            };
                        }

                        break;
                    }

                default:
                    throw new NotSupportedException($"Unsupported UIMessage role: {msg.Role}");
            }
        }
    }

    public static IEnumerable<object> ToDeepbricksMessages(this IEnumerable<ChatMessage> messages)
    {
        foreach (var msg in messages)
        {
            var content = msg.Content.ToDeepbricksContentString();

            if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                yield return new
                {
                    role = msg.Role,
                    content,
                    tool_call_id = msg.ToolCallId
                };

                continue;
            }

            object? toolCalls = null;
            if (msg.ToolCalls is not null)
                toolCalls = NormalizeToolCallsForDeepbricks(msg.ToolCalls);

            yield return new
            {
                role = msg.Role,
                content,
                tool_calls = toolCalls
            };
        }
    }

    public static IEnumerable<object> ToDeepbricksTools(this IEnumerable<Tool> tools)
    {
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name))
                continue;

            yield return new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = tool.InputSchema
                }
            };
        }
    }

    public static IEnumerable<object> ToDeepbricksTools(this IEnumerable<object> tools)
    {
        foreach (var tool in tools)
        {
            var el = JsonSerializer.SerializeToElement(tool, J);

            var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var desc = el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;

            object? parameters = null;
            if (el.TryGetProperty("inputSchema", out var schema) && schema.ValueKind == JsonValueKind.Object)
                parameters = JsonSerializer.Deserialize<object>(schema.GetRawText(), J);
            else if (el.TryGetProperty("parameters", out var parametersEl) && parametersEl.ValueKind == JsonValueKind.Object)
                parameters = JsonSerializer.Deserialize<object>(parametersEl.GetRawText(), J);

            yield return new
            {
                type = "function",
                function = new
                {
                    name,
                    description = desc,
                    parameters
                }
            };
        }
    }

    public static string ToDeepbricksContentString(this JsonElement content)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => ChatMessageContentExtensions.ToText(content) ?? string.Empty
        };
    }

    private static object NormalizeToolCallsForDeepbricks(IEnumerable<object> toolCalls)
    {
        var list = new List<object>();

        foreach (var tc in toolCalls)
        {
            var el = JsonSerializer.SerializeToElement(tc, J);

            var id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;

            var type = el.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String
                ? tEl.GetString()
                : "function";

            if (!el.TryGetProperty("function", out var fnEl) || fnEl.ValueKind != JsonValueKind.Object)
                continue;

            var name = fnEl.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var arguments = "{}";
            if (fnEl.TryGetProperty("arguments", out var argsEl))
            {
                arguments = argsEl.ValueKind switch
                {
                    JsonValueKind.String => argsEl.GetString() ?? "{}",
                    JsonValueKind.Object or JsonValueKind.Array => argsEl.GetRawText(),
                    JsonValueKind.Null or JsonValueKind.Undefined => "{}",
                    _ => argsEl.GetRawText()
                };
            }

            list.Add(new
            {
                id,
                type,
                function = new
                {
                    name,
                    arguments
                }
            });
        }

        return list;
    }
}

