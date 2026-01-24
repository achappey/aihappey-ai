using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AI21;

/// <summary>
/// AI21 requires <c>messages[].content</c> to be a string (not OpenAI-style content parts).
/// This mapper converts gateway ChatCompletions DTOs into AI21 request JSON.
/// </summary>
public static class AI21CompletionsMappingExtensions
{
    private static readonly JsonSerializerOptions J = JsonSerializerOptions.Web;

    /// <summary>
    /// Convert Vercel UI chat request messages into AI21 <c>messages</c>.
    /// AI21 requires a plain string <c>content</c>; we drop any non-text UI parts.
    /// If a UIMessage contains multiple text parts, we emit multiple messages with the same role.
    /// </summary>
    public static IEnumerable<object> ToAi21Messages(this IEnumerable<UIMessage> uiMessages)
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

            foreach (var text in msg.Parts.ToAi21TextParts())
            {
                if (!string.IsNullOrWhiteSpace(text))
                    yield return new { role, content = text };
            }
        }
    }

    private static IEnumerable<string> ToAi21TextParts(this IEnumerable<UIMessagePart> parts)
    {
        foreach (var p in parts)
        {
            if (p is TextUIPart t && !string.IsNullOrWhiteSpace(t.Text))
                yield return t.Text;
        }
    }

    /// <summary>
    /// Convert ChatCompletions messages into AI21 <c>messages</c>.
    /// Rules:
    /// - Always emit <c>content</c> as string.
    /// - Preserve <c>tool_call_id</c> for role=tool messages.
    /// - Preserve <c>tool_calls</c> for assistant messages, but normalize function.arguments to a JSON string
    ///   (AI21 request schema expects JSON string).
    /// </summary>
    public static IEnumerable<object> ToAi21Messages(this IEnumerable<ChatMessage> messages)
    {
        foreach (var msg in messages)
        {
            var role = msg.Role;
            var contentText = msg.Content.ToAi21ContentString();

            // NOTE: AI21 supports tool messages with tool_call_id.
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

            // assistant tool_calls (optional)
            object? toolCalls = null;
            if (msg.ToolCalls is not null)
            {
                toolCalls = NormalizeToolCallsForAi21Request(msg.ToolCalls);
            }

            yield return new
            {
                role,
                content = contentText,
                tool_calls = toolCalls
            };
        }
    }

    /// <summary>
    /// Convert gateway tool definitions to AI21 tool definitions.
    /// Gateway tools are expected to be in Vercel AI SDK shape: {name, description, inputSchema}.
    /// AI21 expects OpenAI-like: { type: "function", function: { name, description, parameters } }.
    /// </summary>
    public static IEnumerable<object> ToAi21Tools(this IEnumerable<object> tools)
    {
        foreach (var tool in tools)
        {
            // tools come in as opaque objects; we normalize by reading properties via JsonElement
            var el = JsonSerializer.SerializeToElement(tool, J);

            var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var desc = el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;

            // inputSchema -> parameters
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

    public static string ToAi21ContentString(this JsonElement content)
    {
        // AI21 requires string content. Gateway content may be:
        // - JsonValue string
        // - array/object content parts (OpenAI Responses style)
        // We stringify non-string content.
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => content.GetRawText()
        };
    }

    private static object NormalizeToolCallsForAi21Request(IEnumerable<object> toolCalls)
    {
        // tool_calls in ChatMessage are opaque; normalize based on OpenAI-ish shape:
        // { id, type, function: { name, arguments } }
        // For AI21 request, function.arguments must be a JSON string.

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

            // arguments can be string or object; convert to JSON string
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

