using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ARKLabs;

public static class ARKLabsCompletionsMappingExtensions
{
    private static readonly JsonSerializerOptions J = JsonSerializerOptions.Web;

    public static IEnumerable<object> ToARKLabsMessages(this IEnumerable<ChatMessage> messages)
    {
        foreach (var msg in messages)
        {
            var role = msg.Role;
            var content = msg.Content.ToARKLabsContentString();

            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                yield return new
                {
                    role,
                    content,
                    tool_call_id = msg.ToolCallId
                };

                continue;
            }

            object? toolCalls = null;
            if (msg.ToolCalls is not null)
                toolCalls = NormalizeToolCallsForRequest(msg.ToolCalls);

            yield return new
            {
                role,
                content,
                tool_calls = toolCalls
            };
        }
    }

    public static IEnumerable<object> ToARKLabsMessages(this IEnumerable<UIMessage> messages)
    {
        foreach (var msg in messages)
        {
            switch (msg.Role)
            {
                case Role.system:
                case Role.user:
                    {
                        var role = msg.Role == Role.system ? "system" : "user";
                        var content = string.Concat(msg.Parts.OfType<TextUIPart>().Select(x => x.Text));

                        if (!string.IsNullOrWhiteSpace(content))
                            yield return new { role, content };

                        break;
                    }

                case Role.assistant:
                    {
                        var contentBuffer = new List<string>();

                        foreach (var part in msg.Parts)
                        {
                            if (part is TextUIPart t)
                            {
                                if (!string.IsNullOrWhiteSpace(t.Text))
                                    contentBuffer.Add(t.Text);

                                continue;
                            }

                            if (part is ToolInvocationPart tip)
                            {
                                if (contentBuffer.Count > 0)
                                {
                                    yield return new
                                    {
                                        role = "assistant",
                                        content = string.Concat(contentBuffer)
                                    };
                                    contentBuffer.Clear();
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
                                    yield return new
                                    {
                                        role = "tool",
                                        tool_call_id = tip.ToolCallId,
                                        name = toolName,
                                        content = tip.Output is string s ? s : JsonSerializer.Serialize(tip.Output, J)
                                    };
                                }
                            }
                        }

                        if (contentBuffer.Count > 0)
                        {
                            yield return new
                            {
                                role = "assistant",
                                content = string.Concat(contentBuffer)
                            };
                        }

                        break;
                    }

                default:
                    throw new NotSupportedException($"Unsupported UIMessage role: {msg.Role}");
            }
        }
    }

    public static IEnumerable<object> ToARKLabsTools(this IEnumerable<object> tools)
    {
        foreach (var tool in tools)
        {
            var el = JsonSerializer.SerializeToElement(tool, J);

            var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var description = el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;

            object? parameters = null;
            if (el.TryGetProperty("inputSchema", out var schema) && schema.ValueKind == JsonValueKind.Object)
                parameters = JsonSerializer.Deserialize<object>(schema.GetRawText(), J);

            yield return new
            {
                type = "function",
                function = new
                {
                    name,
                    description,
                    parameters
                }
            };
        }
    }

    public static string ToARKLabsContentString(this JsonElement content)
    {
        if (content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return string.Empty;

        return ChatMessageContentExtensions.ToText(content) ?? string.Empty;
    }

    private static object NormalizeToolCallsForRequest(IEnumerable<object> toolCalls)
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

            string argsJson = "{}";
            if (fnEl.TryGetProperty("arguments", out var aEl))
            {
                argsJson = aEl.ValueKind switch
                {
                    JsonValueKind.String => aEl.GetString() ?? "{}",
                    JsonValueKind.Object or JsonValueKind.Array => aEl.GetRawText(),
                    JsonValueKind.Null or JsonValueKind.Undefined => "{}",
                    _ => aEl.GetRawText()
                };
            }

            list.Add(new
            {
                id,
                type,
                function = new
                {
                    name,
                    arguments = argsJson
                }
            });
        }

        return list;
    }
}

