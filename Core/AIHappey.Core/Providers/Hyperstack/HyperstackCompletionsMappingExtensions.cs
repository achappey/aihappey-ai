using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Hyperstack;

public static class HyperstackCompletionsMappingExtensions
{
    private static readonly JsonSerializerOptions J = JsonSerializerOptions.Web;

    /// <summary>
    /// Maps your internal UI messages to Together/OpenAI "messages" array.
    /// - user/system: content as array of parts (text, image_url)
    /// - assistant: streams parts in order; flushes a content array up to a ToolInvocationPart,
    ///              then emits an assistant message with tool_calls; if tool output present,
    ///              appends a separate role:"tool" message with tool_call_id + serialized content.
    /// </summary>
    public static IEnumerable<object> ToCompletionMessages(this IEnumerable<UIMessage> uiMessages)
    {
        foreach (var msg in uiMessages)
        {
            switch (msg.Role)
            {
                case Role.system:
                    {
                        var parts = msg.Parts.ToCompletionContentParts().OfType<string>().ToList();
                        foreach (var s in parts)
                        {
                            yield return new { role = "system", content = s };
                        }

                        break;
                    }

                case Role.user:
                    {
                        var parts = msg.Parts.ToCompletionContentParts().OfType<string>().ToList();
                        foreach (var s in parts)
                        {
                            yield return new { role = "user", content = s };
                        }

                        break;
                    }

                case Role.assistant:
                    {
                        var parts = msg.Parts.ToCompletionContentParts().OfType<string>().ToList();
                        foreach (var s in parts)
                        {
                            yield return new { role = "assistant", content = s };
                        }


                        break;
                    }

                default:
                    throw new NotSupportedException($"Unsupported UIMessage role: {msg.Role}");
            }
        }
    }

    public static IEnumerable<string> ToCompletionContentParts(this IEnumerable<UIMessagePart> parts)
    {
        foreach (var p in parts)
        {
            var mapped = MapPartToCompletionContent(p);
            if (mapped is not null)
                yield return mapped;
        }
    }

    /// <summary>
    /// Maps ChatCompletions messages into Hyperstack "messages" (text-only).
    /// - Drops tool calls, tool role, and non-text content parts.
    /// - Flattens multi-part content into multiple messages with the same role.
    /// </summary>
    public static IEnumerable<object> ToHyperstackMessages(this IEnumerable<ChatMessage> messages)
    {
        foreach (var msg in messages)
        {
            if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase))
                continue;

            if (msg.ToolCalls is not null)
                continue;

            foreach (var text in MapChatMessageContent(msg.Content))
            {
                if (!string.IsNullOrWhiteSpace(text))
                    yield return new { role = msg.Role, content = text };
            }
        }
    }

    // --- helpers -------------------------------------------------------------

    private static string? MapPartToCompletionContent(UIMessagePart part)
    {
        return part switch
        {
            TextUIPart t when !string.IsNullOrWhiteSpace(t.Text) => t.Text,
            _ => null,
        };
    }

    private static IEnumerable<string> MapChatMessageContent(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                {
                    var text = content.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        yield return text;
                    yield break;
                }
            case JsonValueKind.Array:
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        switch (part.ValueKind)
                        {
                            case JsonValueKind.String:
                                {
                                    var text = part.GetString();
                                    if (!string.IsNullOrWhiteSpace(text))
                                        yield return text;
                                    break;
                                }
                            case JsonValueKind.Object:
                                {
                                    if (TryGetTextPart(part, out var text) && !string.IsNullOrWhiteSpace(text))
                                        yield return text!;
                                    break;
                                }
                        }
                    }

                    yield break;
                }
            case JsonValueKind.Object:
                {
                    if (TryGetTextPart(content, out var text) && !string.IsNullOrWhiteSpace(text))
                        yield return text!;
                    yield break;
                }
        }
    }

    private static bool TryGetTextPart(JsonElement part, out string? text)
    {
        text = null;

        if (part.TryGetProperty("type", out var typeProp)
            && typeProp.ValueKind == JsonValueKind.String
            && string.Equals(typeProp.GetString(), "text", StringComparison.OrdinalIgnoreCase)
            && part.TryGetProperty("text", out var textProp)
            && textProp.ValueKind == JsonValueKind.String)
        {
            text = textProp.GetString();
            return true;
        }

        if (part.TryGetProperty("text", out var plainText)
            && plainText.ValueKind == JsonValueKind.String)
        {
            text = plainText.GetString();
            return true;
        }

        return false;
    }

}
