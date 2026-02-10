using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Cortecs;

public static class CortecsCompletionsMappingExtensions
{
    private static readonly JsonSerializerOptions J = JsonSerializerOptions.Web;

    public static IEnumerable<object> ToCortecsMessages(this IEnumerable<ChatMessage> messages)
    {
        foreach (var msg in messages)
        {
            var role = msg.Role;
            var contentText = msg.Content.ToCortecsContentString();

            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                yield return new
                {
                    role,
                    content = contentText,
                    tool_call_id = msg.ToolCallId
                };

                continue;
            }

            object? toolCalls = null;
            if (msg.ToolCalls is not null)
                toolCalls = NormalizeToolCallsForCortecsRequest(msg.ToolCalls);

            yield return new
            {
                role,
                content = contentText,
                tool_calls = toolCalls
            };
        }
    }

    public static IEnumerable<object> ToCortecsMessages(this IEnumerable<UIMessage> uiMessages)
    {
        foreach (var msg in uiMessages)
        {
            var role = msg.Role switch
            {
                Role.system => "system",
                Role.user => "user",
                Role.assistant => "assistant",
                _ => throw new NotSupportedException($"Unsupported UIMessage role: {msg.Role}")
            };

            foreach (var text in msg.Parts.ToCortecsTextParts())
            {
                if (!string.IsNullOrWhiteSpace(text))
                    yield return new { role, content = text };
            }
        }
    }

    public static IEnumerable<object> ToCortecsTools(this IEnumerable<object> tools)
    {
        foreach (var tool in tools)
        {
            var el = JsonSerializer.SerializeToElement(tool, J);

            if (el.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String
                && string.Equals(typeEl.GetString(), "function", StringComparison.OrdinalIgnoreCase)
                && el.TryGetProperty("function", out var fnEl)
                && fnEl.ValueKind == JsonValueKind.Object)
            {
                yield return JsonSerializer.Deserialize<object>(el.GetRawText(), J)!;
                continue;
            }

            var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var desc = el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;

            object? parameters = null;
            if (el.TryGetProperty("inputSchema", out var schema) && schema.ValueKind is JsonValueKind.Object)
                parameters = JsonSerializer.Deserialize<object>(schema.GetRawText(), J);

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

    public static IEnumerable<object> ToCortecsTools(this IEnumerable<Tool> tools)
        => tools.Select(a => new
        {
            type = "function",
            function = new
            {
                name = a.Name,
                description = a.Description,
                parameters = a.InputSchema
            }
        });

    public static string ToCortecsContentString(this JsonElement content)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => content.GetRawText()
        };
    }

    public static string? ExtractCortecsText(this JsonElement content)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Concat(
                content.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String
                        ? item.GetString()
                        : item.ValueKind == JsonValueKind.Object
                            && item.TryGetProperty("text", out var textEl)
                            && textEl.ValueKind == JsonValueKind.String
                                ? textEl.GetString()
                                : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))),
            JsonValueKind.Object when content.TryGetProperty("text", out var textObj)
                                       && textObj.ValueKind == JsonValueKind.String => textObj.GetString(),
            _ => null
        };
    }

    private static IEnumerable<string> ToCortecsTextParts(this IEnumerable<UIMessagePart> parts)
    {
        foreach (var p in parts)
        {
            if (p is TextUIPart t && !string.IsNullOrWhiteSpace(t.Text))
                yield return t.Text;
        }
    }

    private static object NormalizeToolCallsForCortecsRequest(IEnumerable<object> toolCalls)
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

