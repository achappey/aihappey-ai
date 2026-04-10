using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.ChatCompletions.Mapping;

public static partial class ChatCompletionsUnifiedMapper
{
    private static IEnumerable<AIInputItem> ParseRequestMessages(JsonElement messages)
    {
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object)
                continue;

            var role = ExtractValue<string>(message, "role");
            var content = message.TryGetProperty("content", out var contentEl)
                ? ParseContentParts(contentEl).ToList()
                : [];

            var itemMetadata = new Dictionary<string, object?>
            {
                ["chatcompletions.message.raw"] = message.Clone()
            };

            foreach (var prop in message.EnumerateObject())
            {
                if (prop.Name is "role" or "content")
                    continue;

                itemMetadata[$"chatcompletions.message.{prop.Name}"] = prop.Value.Clone();
            }

            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase)
                && message.TryGetProperty("tool_call_id", out var toolCallIdEl)
                && toolCallIdEl.ValueKind == JsonValueKind.String)
            {
                yield return new AIInputItem
                {
                    Type = "function_call_output",
                    Role = role,
                    Content =
                    [
                        CreateToolOutputPart(
                            toolCallIdEl.GetString() ?? string.Empty,
                            message.TryGetProperty("content", out var toolContent) ? toolContent : default,
                            itemMetadata)
                    ],
                    Metadata = itemMetadata
                };

                continue;
            }

            if (message.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
                content.AddRange(ParseToolCallParts(toolCallsEl));

            yield return new AIInputItem
            {
                Type = "message",
                Role = role,
                Content = content,
                Metadata = itemMetadata
            };
        }
    }

    private static IEnumerable<AIContentPart> ParseContentParts(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return new AITextContentPart
                        {
                            Text = text,
                            Type = "text",
                            Metadata = new Dictionary<string, object?>
                            {
                                ["chatcompletions.part.raw"] = content.Clone()
                            }
                        };
                    }

                    yield break;
                }
            case JsonValueKind.Array:
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("type", out var typeEl)
                        && typeEl.ValueKind == JsonValueKind.String)
                    {
                        var type = typeEl.GetString();
                        if (type == "text" && part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                        {
                            yield return new AITextContentPart
                            {
                                Type = "text",
                                Text = textEl.GetString() ?? string.Empty,
                                Metadata = new Dictionary<string, object?>
                                {
                                    ["chatcompletions.part.raw"] = part.Clone()
                                }
                            };
                            continue;
                        }

                        yield return new AIFileContentPart
                        {
                            MediaType = "application/json",
                            Data = part.Clone(),
                            Type = "file",
                            Metadata = new Dictionary<string, object?>
                            {
                                ["chatcompletions.part.type"] = type,
                                ["chatcompletions.part.raw"] = part.Clone()
                            }
                        };
                        continue;
                    }

                    yield return new AIFileContentPart
                    {
                        MediaType = "application/json",
                        Type = "file",
                        Data = part.Clone(),
                        Metadata = new Dictionary<string, object?>
                        {
                            ["chatcompletions.part.raw"] = part.Clone()
                        }
                    };
                }

                yield break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                yield break;
            default:
                yield return new AIFileContentPart
                {
                    MediaType = "application/json",
                    Data = content.Clone(),
                    Type = "file",
                    Metadata = new Dictionary<string, object?>
                    {
                        ["chatcompletions.part.raw"] = content.Clone()
                    }
                };
                yield break;
        }
    }

    private static IEnumerable<AIToolDefinition> ParseTools(JsonElement tools)
    {
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
                continue;

            var type = ExtractValue<string>(tool, "type") ?? "function";
            var function = tool.TryGetProperty("function", out var fn) ? fn : default;
            var custom = tool.TryGetProperty("custom", out var customEl) ? customEl : default;
            var source = function.ValueKind == JsonValueKind.Object ? function : custom;

            var name = ExtractValue<string>(source, "name") ?? "tool";

            yield return new AIToolDefinition
            {
                Name = name,
                Description = ExtractValue<string>(source, "description"),
                InputSchema = source.TryGetProperty("parameters", out var parameters) ? parameters.Clone() : null,
                Metadata = new Dictionary<string, object?>
                {
                    ["chatcompletions.tool.raw"] = tool.Clone(),
                    ["chatcompletions.tool.type"] = type,
                    ["chatcompletions.tool.function"] = function.ValueKind == JsonValueKind.Object ? function.Clone() : null,
                    ["chatcompletions.tool.custom"] = custom.ValueKind == JsonValueKind.Object ? custom.Clone() : null
                }
            };
        }
    }

    private static IEnumerable<ChatMessage> ToChatMessages(AIInput? input)
    {
        if (input?.Items is null)
            return Enumerable.Empty<ChatMessage>();

        var list = new List<ChatMessage>();

        foreach (var item in input.Items)
        {
            var toolParts = (item.Content ?? []).OfType<AIToolCallContentPart>().ToList();

            if (string.Equals(item.Type, "function_call_output", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var toolPart in toolParts.Where(a => a.IsClientToolCall))
                {
                    list.Add(new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = toolPart.ToolCallId,
                        Content = SerializeJsonElement(ToolOutputToChatContent(toolPart) ?? string.Empty),
                    });
                }

                continue;
            }

            if (!string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(item.Role))
                continue;

            var role = item.Role ?? "user";
            var nonToolParts = (item.Content ?? []).Where(a => a is not AIToolCallContentPart).ToList();
            var toolCalls = BuildOutboundToolCalls(toolParts, item.Metadata);
            var content = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                          && toolCalls is { Count: > 0 }
                          && nonToolParts.Count == 0
                ? SerializeJsonElement((object?)null)
                : ToChatMessageContent(nonToolParts, role);

            var toolCallId = ExtractMetadataValue<string>(item.Metadata, "chatcompletions.message.tool_call_id");

            list.Add(new ChatMessage
            {
                Role = role,
                Content = content,
                ToolCallId = toolCallId,
                ToolCalls = toolCalls
            });
        }

        return list;
    }

    private static JsonElement ToChatMessageContent(IEnumerable<AIContentPart>? parts, string role)
    {
        var list = (parts ?? []).ToList();
        if (list.Count == 0)
            return JsonSerializer.SerializeToElement(string.Empty, Json);

        if (list.Count == 1 && list[0] is AITextContentPart textOnly)
            return JsonSerializer.SerializeToElement(textOnly.Text, Json);

        var mapped = new List<object>();

        foreach (var part in list)
        {
            /*if (part.Metadata is not null && part.Metadata.TryGetValue("chatcompletions.part.raw", out var rawPart) && rawPart is not null)
            {
                mapped.Add(rawPart);
                continue;
            }*/

            if (part is AITextContentPart text)
            {
                mapped.Add(new { type = "text", text = text.Text });
                continue;
            }

            if (part is AIFileContentPart file)
            {
                if (role == "user")
                {
                    if (file.MediaType?.StartsWith("image/") == true)
                    {
                        mapped.Add(new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = file.Data
                            }
                        });
                    }
                    else if (file.MediaType?.StartsWith("audio/") == true)
                    {
                        var format = !string.IsNullOrEmpty(file.Filename)
                                    ? Path.GetExtension(file.Filename).TrimStart('.')
                                    : file.MediaType.Split("/").Last() == "mpeg"
                                    ? "mp3" : file.MediaType.Split("/").Last();

                        mapped.Add(new
                        {
                            type = "input_audio",
                            input_audio = new
                            {
                                format = format,
                                data = file.Data
                            }
                        });
                    }
                    else
                    {
                        mapped.Add(new
                        {
                            type = "file",
                            file = new
                            {
                                filename = file.Filename,
                                file_data = file.Data
                            }
                        });
                    }

                }
            }
        }

        return JsonSerializer.SerializeToElement(mapped, Json);
    }

    private static IEnumerable<object> ToChatTools(List<AIToolDefinition>? tools)
    {
        if (tools is null)
            return Enumerable.Empty<object>();

        return tools.Select(ToRawChatTool).ToList();
    }

    private static object ToRawChatTool(AIToolDefinition tool)
    {
        if (tool.Metadata is not null
            && tool.Metadata.TryGetValue("chatcompletions.tool.raw", out var raw)
            && raw is not null)
            return raw;

        return new
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

    private static AIOutput? ParseChunkOutput(JsonElement chunk)
    {
        if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return null;

        var items = new List<AIOutputItem>();

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                continue;

            var contentParts = new List<AIContentPart>();

            if (delta.TryGetProperty("content", out var contentEl))
            {
                contentParts.AddRange(ParseContentParts(contentEl));
            }

            if (delta.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
                contentParts.AddRange(ParseToolCallParts(toolCallsEl));

            var role = ExtractValue<string>(delta, "role") ?? "assistant";
            var metadata = new Dictionary<string, object?>
            {
                ["chatcompletions.delta.raw"] = delta.Clone(),
                ["chatcompletions.choice.raw"] = choice.Clone(),
                ["chatcompletions.choice.index"] = ExtractValue<int?>(choice, "index"),
                ["chatcompletions.choice.finish_reason"] = ExtractValue<string>(choice, "finish_reason")
            };

            items.Add(new AIOutputItem
            {
                Type = "message.delta",
                Role = role,
                Content = contentParts,
                Metadata = metadata
            });
        }

        return items.Count > 0 ? new AIOutput { Items = items } : null;
    }

    private static IEnumerable<AIToolCallContentPart> ParseToolCallParts(JsonElement toolCalls)
    {
        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            if (toolCall.ValueKind != JsonValueKind.Object)
                continue;

            var function = toolCall.TryGetProperty("function", out var functionEl)
                && functionEl.ValueKind == JsonValueKind.Object
                ? functionEl
                : default;

            yield return new AIToolCallContentPart
            {
                Type = "tool-call",
                ToolCallId = ExtractValue<string>(toolCall, "id") ?? Guid.NewGuid().ToString("N"),
                ToolName = ExtractValue<string>(function, "name"),
                Title = ExtractValue<string>(function, "name"),
                Input = ParseToolArguments(ExtractValue<string>(function, "arguments")),
                State = "input-available",
                ProviderExecuted = false,
                Metadata = new Dictionary<string, object?>
                {
                    ["chatcompletions.tool_call.raw"] = toolCall.Clone()
                }
            };
        }
    }

    private static AIToolCallContentPart CreateToolOutputPart(
        string toolCallId,
        JsonElement content,
        Dictionary<string, object?> itemMetadata)
    {
        return new AIToolCallContentPart
        {
            Type = "tool-output-available",
            ToolCallId = toolCallId,
            Output = ParseToolOutputContent(content),
            State = "output-available",
            ProviderExecuted = false,
            Metadata = new Dictionary<string, object?>
            {
                ["chatcompletions.tool_output.raw"] = itemMetadata.TryGetValue("chatcompletions.message.raw", out var raw) ? raw : null
            }
        };
    }

    private static List<object>? BuildOutboundToolCalls(
        List<AIToolCallContentPart> toolParts,
        Dictionary<string, object?>? metadata)
    {
        var clientToolCalls = toolParts
            .Where(a => a.IsClientToolCall)
            .Where(a => !string.IsNullOrWhiteSpace(a.ToolCallId))
            .Select(ToOutboundToolCall)
            .ToList();

        if (clientToolCalls.Count > 0)
            return clientToolCalls;

        var rawToolCalls = ExtractMetadataElement(metadata, "chatcompletions.message.tool_calls");
        return rawToolCalls is { ValueKind: JsonValueKind.Array }
            ? rawToolCalls.Value.EnumerateArray().Select(e => (object)e.Clone()).ToList()
            : null;
    }

    private static object ToOutboundToolCall(AIToolCallContentPart toolCall)
    {
        return new
        {
            id = string.IsNullOrWhiteSpace(toolCall.ToolCallId) ? Guid.NewGuid().ToString("N") : toolCall.ToolCallId,
            type = "function",
            function = new
            {
                name = toolCall.ToolName ?? toolCall.Title ?? "tool",
                arguments = SerializeToolPayload(toolCall.Input, "{}")
            }
        };
    }

    private static object? ParseToolArguments(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return JsonSerializer.SerializeToElement(new { }, Json);

        try
        {
            return JsonDocument.Parse(raw).RootElement.Clone();
        }
        catch
        {
            return raw;
        }
    }

    private static object? ParseToolOutputContent(JsonElement content)
    {
        var text = ChatMessageContentExtensions.ToText(content);
        if (!string.IsNullOrWhiteSpace(text))
            return text;

        return content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : content.Clone();
    }

    private static object? ToolOutputToChatContent(AIToolCallContentPart toolCall)
    {
        return toolCall.Output switch
        {
            null => string.Empty,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString() ?? string.Empty,
            JsonElement json => json.GetRawText(),
            string text => text,
            _ => JsonSerializer.Serialize(toolCall.Output, Json)
        };
    }

    private static string SerializeToolPayload(object? value, string fallback)
    {
        return value switch
        {
            null => fallback,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString() ?? fallback,
            JsonElement json => json.GetRawText(),
            string text => text,
            _ => JsonSerializer.Serialize(value, Json)
        };
    }

    private static JsonElement SerializeJsonElement(object? value)
        => JsonSerializer.SerializeToElement(value, Json);
}
