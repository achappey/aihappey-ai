using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Messages.Mapping;

public static partial class MessagesUnifiedMapper
{
    private static IEnumerable<AIContentPart> ToUnifiedContentParts(MessagesContent content, string? providerId = null)
    {
        if (content.IsText && !string.IsNullOrWhiteSpace(content.Text))
        {
            yield return new AITextContentPart
            {
                Type = "text",
                Text = content.Text!,
                Metadata = new Dictionary<string, object?>
                {
                    ["messages.content.kind"] = "text"
                }
            };
            yield break;
        }

        foreach (var block in content.Blocks ?? [])
        {
            if (TryCreateUnifiedToolCallPart(block, out var toolPart))
            {
                yield return toolPart;
                continue;
            }

            switch (block.Type)
            {
                case "text":
                    yield return new AITextContentPart
                    {
                        Type = "text",
                        Text = block.Text ?? string.Empty,
                        Metadata = CreateBlockMetadata(block, providerId)
                    };
                    break;
                case "thinking":
                case "redacted_thinking":
                    yield return new AIReasoningContentPart
                    {
                        Type = "reasoning",
                        Text = block.Thinking ?? block.Data,
                        Metadata = CreateBlockMetadata(block, providerId)
                    };
                    break;
                case "image":
                case "document":
                case "container_upload":
                    yield return ToUnifiedFilePart(block);
                    break;
                default:
                    yield return new AIFileContentPart
                    {
                        Type = "file",
                        MediaType = "application/json",
                        Filename = block.Title ?? block.Name ?? block.Id,
                        Data = JsonSerializer.SerializeToElement(block, Json),
                        Metadata = CreateBlockMetadata(block, providerId)
                    };
                    break;
            }
        }
    }

    private static AIToolDefinition ToUnifiedTool(MessageToolDefinition tool)
        => new()
        {
            Name = tool.Name ?? tool.Type ?? "tool",
            Title = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
            Metadata = new Dictionary<string, object?>
            {
                ["messages.tool.raw"] = JsonSerializer.SerializeToElement(tool, Json),
                ["messages.tool.type"] = tool.Type
            }
        };

    private static MessageToolDefinition ToMessageTool(AIToolDefinition tool)
    {
        if (tool.Metadata is not null && tool.Metadata.TryGetValue("messages.tool.raw", out var raw) && raw is not null)
        {
            var hydrated = DeserializeFromObject<MessageToolDefinition>(raw);
            if (hydrated is not null)
                return hydrated;
        }

        return new MessageToolDefinition
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = SerializeToNullableElement(tool.InputSchema),
            Type = ExtractValue<string>(tool.Metadata, "messages.tool.type") ?? "custom"
        };
    }

    private static Dictionary<string, object?> CreateBlockMetadata(MessageContentBlock block, string? providerId = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["messages.block.type"] = block.Type,
            ["messages.block.raw"] = JsonSerializer.SerializeToElement(block, Json)
        };

        if (IsToolOutputBlock(block.Type) && block.Type != "tool_result" && !string.IsNullOrWhiteSpace(providerId))
        {
            metadata["messages.provider.id"] = providerId;
            metadata["messages.provider.metadata"] = CreateToolOutputProviderMetadata(providerId, block);
        }

        return metadata;
    }

    private static MessageContentBlock? ExtractRawBlock(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("messages.block.raw", out var raw) || raw is null)
            return null;

        return DeserializeFromObject<MessageContentBlock>(raw);
    }

    private static MessageContentBlock? ToMessageFileBlock(AIFileContentPart file)
    {
        var raw = ExtractRawBlock(file.Metadata);
        if (raw is not null)
            return raw;

        if (file.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new MessageContentBlock
            {
                Type = "image",
                Source = new MessageSource
                {
                    Type = "base64",
                    Data = file.Data?.ToString().StripBase64Prefix(),
                    MediaType = file.MediaType
                }
            };
        }

        if (file.MediaType?.StartsWith("application/pdf", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new MessageContentBlock
            {
                Type = "document",
                Source = new MessageSource
                {
                    Type = "base64",
                    Data = file.Data?.ToString().StripBase64Prefix(),
                    MediaType = file.MediaType
                },
                Title = file.Filename
            };
        }

        return new MessageContentBlock
        {
            Type = "document",
            Source = new MessageSource
            {
                Type = file.Data?.ToString()?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? "url" : "text",
                Url = file.Data?.ToString()?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? file.Data?.ToString() : null,
                Data = file.Data?.ToString()?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? null : file.Data?.ToString(),
                MediaType = file.MediaType ?? "text/plain"
            },
            Title = file.Filename,
            FileId = file.Filename
        };
    }

    private static object SerializeBlockOutput(MessageContentBlock block)
    {
        if (block.Type == "web_search_tool_result")
        {
            var results = block.Content?.IsBlocks == true
                ? block.Content.Blocks
                : null;

            return JsonSerializer.SerializeToElement(new
            {
                structuredContent = new
                {
                    content = results
                }
            }, Json);
        }

        if (block.Content is { IsText: true })
            return block.Content.Text ?? string.Empty;

        if (block.Content is { IsBlocks: true })
            return JsonSerializer.SerializeToElement(block.Content.Blocks, Json);

        if (block.Content is { IsRaw: true })
            return block.Content.Raw!.Value.Clone();

        return JsonSerializer.SerializeToElement(block, Json);
    }

    private static bool TryCreateUnifiedToolCallPart(
        MessageContentBlock block,
        out AIToolCallContentPart toolPart)
    {
        if (IsToolInputBlock(block.Type))
        {
            toolPart = new AIToolCallContentPart
            {
                Type = block.Type,
                ToolCallId = block.Id ?? block.ToolUseId ?? Guid.NewGuid().ToString("N"),
                ToolName = block.Name ?? block.ToolName,
                Title = block.Title ?? block.Name,
                Input = block.Input?.Clone(),
                State = "input-available",
                ProviderExecuted = IsProviderExecutedTool(block.Type),
                Metadata = CreateBlockMetadata(block)
            };

            return true;
        }

        if (IsToolOutputBlock(block.Type))
        {
            toolPart = new AIToolCallContentPart
            {
                Type = block.Type,
                ToolCallId = block.ToolUseId ?? block.Id ?? Guid.NewGuid().ToString("N"),
                ToolName = block.Name ?? block.ToolName,
                Title = block.Title ?? block.Name,
                Output = block.IsError == true
                    ? JsonSerializer.SerializeToElement(new { error = FlattenContentText(block.Content) }, Json)
                    : SerializeBlockOutput(block),
                State = block.IsError == true ? "output-error" : "output-available",
                ProviderExecuted = block.Type != "tool_result",
                Metadata = CreateBlockMetadata(block)
            };

            return true;
        }

        toolPart = null!;
        return false;
    }

    private static IEnumerable<(MessageContentBlock? AssistantBlock, MessageContentBlock? UserBlock)> ToMessageToolBlocks(AIToolCallContentPart toolPart)
    {
        if (toolPart.IsProviderToolCall)
        {
            if (TryCreateProviderExecutedToolUseBlock(toolPart, out var providerToolUseBlock))
                yield return (providerToolUseBlock, null);

            if (TryCreateProviderExecutedToolResultBlock(toolPart, out var providerResultBlock))
                yield return (providerResultBlock, null);

            yield break;
        }

        yield return (
            new MessageContentBlock
            {
                Type = "tool_use",
                Id = toolPart.ToolCallId,
                Name = toolPart.ToolName ?? toolPart.Title ?? "tool",
                Input = SerializeToNullableElement(toolPart.Input) ?? JsonSerializer.SerializeToElement(new { }, Json)
            },
            null);

        if (!HasToolOutput(toolPart))
            yield break;

        yield return (
            null,
            new MessageContentBlock
            {
                Type = "tool_result",
                ToolUseId = toolPart.ToolCallId,
                Content = ToMessageToolOutputContent(toolPart),
                IsError = string.Equals(toolPart.State, "output-error", StringComparison.OrdinalIgnoreCase)
            });
    }

    private static MessagesContent CreateMessagesContentFromBlocks(List<MessageContentBlock> blocks)
        => blocks.Count == 1 && blocks[0].Type == "text"
            ? new MessagesContent(blocks[0].Text ?? string.Empty)
            : new MessagesContent(blocks);

    private static MessagesContent ToMessageToolOutputContent(AIToolCallContentPart toolPart)
    {
        return toolPart.Output switch
        {
            null => new MessagesContent(string.Empty),
            JsonElement json when json.ValueKind == JsonValueKind.String => new MessagesContent(json.GetString() ?? string.Empty),
            JsonElement json => new MessagesContent(json.GetRawText()),
            string text => new MessagesContent(text),
            _ => new MessagesContent(JsonSerializer.SerializeToElement(toolPart.Output, Json).GetRawText())
        };
    }

    private static bool HasToolOutput(AIToolCallContentPart toolPart)
        => toolPart.Output is not null;

    private static bool TryCreateProviderExecutedToolResultBlock(
        AIToolCallContentPart toolPart,
        out MessageContentBlock? block)
    {
        block = null;

        if (!TryGetMatchingProviderMetadata(toolPart.Metadata, out var providerMetadata))
            return false;

        var blockType = ExtractValue<string>(toolPart.Metadata, "messages.block.type") ?? "tool_result";
        if (!TryExtractProviderExecutedMessagesContent(toolPart.Output, blockType, out var content))
            return false;

        var raw = ExtractRawBlock(toolPart.Metadata);
        block = raw ?? new MessageContentBlock();
        block.Type = blockType;
        block.ToolUseId = toolPart.ToolCallId;
        block.Content = content;
        block.IsError = SupportsOuterIsErrorOnToolResultBlock(blockType)
            ? string.Equals(toolPart.State, "output-error", StringComparison.OrdinalIgnoreCase)
            : null;
        ApplyProviderExecutedBlockMetadata(block, providerMetadata);

        return true;
    }

    private static bool TryCreateProviderExecutedToolUseBlock(
        AIToolCallContentPart toolPart,
        out MessageContentBlock? block)
    {
        block = null;

        if (!TryGetMatchingProviderMetadata(toolPart.Metadata, out var providerMetadata))
            return false;

        var resultBlockType = ExtractValue<string>(toolPart.Metadata, "messages.block.type") ?? string.Empty;
        var inputBlockType = ResolveProviderExecutedInputBlockType(resultBlockType, providerMetadata);
        if (string.IsNullOrWhiteSpace(inputBlockType))
            return false;

        block = new MessageContentBlock
        {
            Type = inputBlockType,
            Id = toolPart.ToolCallId,
            Name = toolPart.ToolName ?? toolPart.Title ?? ResolveProviderExecutedToolName(resultBlockType),
            Title = SupportsTitleOnProviderExecutedInputBlock(inputBlockType) ? toolPart.Title : null,
            Input = SerializeToNullableElement(toolPart.Input) ?? JsonSerializer.SerializeToElement(new { }, Json)
        };

        ApplyProviderExecutedInputBlockMetadata(block, providerMetadata);
        return true;
    }

    private static bool TryExtractProviderExecutedMessagesContent(
        object? output,
        string blockType,
        out MessagesContent? content)
    {
        content = null;

        if (!TryGetProviderExecutedPayload(output, out var payload))
            payload = CreateProviderExecutedSuccessPayload(blockType);

        content = ToProviderExecutedMessagesContent(payload, blockType);
        return true;
    }

    private static MessagesContent ToProviderExecutedMessagesContent(JsonElement payload, string blockType)
    {
        payload = NormalizeProviderExecutedPayloadShape(payload, blockType);

        if (payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new MessagesContent(string.Empty);

        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("type", out var innerType)
            && innerType.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(innerType.GetString()))
        {
            return new MessagesContent(payload.Clone());
        }

        if (payload.ValueKind == JsonValueKind.Object
            && ShouldUnwrapProviderExecutedContentProperty(blockType, payload)
            && payload.TryGetProperty("content", out var nestedContent))
        {
            return ToMessagesContentPreservingShape(nestedContent);
        }

        return ToMessagesContentPreservingShape(payload);
    }

    private static JsonElement NormalizeProviderExecutedPayloadShape(JsonElement payload, string blockType)
        => blockType switch
        {
            "web_search_tool_result" => NormalizeWebSearchPayload(payload),
            "bash_code_execution_tool_result" => NormalizeTypedObjectPayload(
                payload,
                successType: "bash_code_execution_result",
                errorType: "bash_code_execution_tool_result_error",
                contentItemType: "bash_code_execution_output"),
            "code_execution_tool_result" => NormalizeCodeExecutionPayload(payload),
            "text_editor_code_execution_tool_result" => NormalizeTextEditorPayload(payload),
            "tool_search_tool_result" => NormalizeTypedObjectPayload(
                payload,
                successType: "tool_search_tool_search_result",
                errorType: "tool_search_tool_result_error",
                contentPropertyName: "tool_references",
                contentItemType: "tool_reference"),
            "web_fetch_tool_result" => NormalizeTypedObjectPayload(
                payload,
                successType: "web_fetch_result",
                errorType: "web_fetch_tool_result_error"),
            "advisor_tool_result" => NormalizeTypedObjectPayload(
                payload,
                successType: "advisor_result",
                errorType: "advisor_tool_result_error"),
            _ => payload
        };

    private static JsonElement CreateProviderExecutedSuccessPayload(string blockType)
        => blockType switch
        {
            "bash_code_execution_tool_result" => JsonSerializer.SerializeToElement(new
            {
                type = "bash_code_execution_result",
                stdout = string.Empty,
                stderr = string.Empty,
                return_code = 0,
                content = Array.Empty<object>()
            }, Json),
            "code_execution_tool_result" => JsonSerializer.SerializeToElement(new
            {
                type = "code_execution_result",
                stdout = string.Empty,
                stderr = string.Empty,
                return_code = 0,
                content = Array.Empty<object>()
            }, Json),
            "tool_search_tool_result" => JsonSerializer.SerializeToElement(new
            {
                type = "tool_search_tool_search_result",
                tool_references = Array.Empty<object>()
            }, Json),
            _ => JsonSerializer.SerializeToElement(new
            {
                type = blockType
            }, Json)
        };

    private static JsonElement NormalizeWebSearchPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array)
            return payload;

        var changed = false;
        var normalized = new List<object?>();

        foreach (var item in payload.EnumerateArray())
        {
            var normalizedItem = EnsureObjectType(item, "web_search_result");
            changed |= normalizedItem.ValueKind != item.ValueKind || normalizedItem.GetRawText() != item.GetRawText();
            normalized.Add(DeserializeToUntypedObject(normalizedItem));
        }

        return changed ? JsonSerializer.SerializeToElement(normalized, Json) : payload;
    }

    private static JsonElement NormalizeCodeExecutionPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return payload;

        if (payload.TryGetProperty("encrypted_stdout", out _))
        {
            return NormalizeTypedObjectPayload(
                payload,
                successType: "encrypted_code_execution_result",
                errorType: "code_execution_tool_result_error",
                contentItemType: "code_execution_output");
        }

        return NormalizeTypedObjectPayload(
            payload,
            successType: "code_execution_result",
            errorType: "code_execution_tool_result_error",
            contentItemType: "code_execution_output");
    }

    private static JsonElement NormalizeTextEditorPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return payload;

        if (payload.TryGetProperty("type", out var existingType)
            && existingType.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(existingType.GetString()))
        {
            return payload;
        }

        if (payload.TryGetProperty("error_code", out _))
            return AddObjectType(payload, "text_editor_code_execution_tool_result_error");

        if (payload.TryGetProperty("file_type", out _))
            return AddObjectType(payload, "text_editor_code_execution_view_result");

        if (payload.TryGetProperty("is_file_update", out _))
            return AddObjectType(payload, "text_editor_code_execution_create_result");

        if (payload.TryGetProperty("lines", out _)
            || payload.TryGetProperty("new_lines", out _)
            || payload.TryGetProperty("new_start", out _)
            || payload.TryGetProperty("old_lines", out _)
            || payload.TryGetProperty("old_start", out _))
        {
            return AddObjectType(payload, "text_editor_code_execution_str_replace_result");
        }

        return payload;
    }

    private static JsonElement NormalizeTypedObjectPayload(
        JsonElement payload,
        string successType,
        string errorType,
        string contentPropertyName = "content",
        string? contentItemType = null)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return payload;

        if (payload.TryGetProperty("type", out var existingType)
            && existingType.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(existingType.GetString()))
        {
            return contentItemType is null
                ? payload
                : EnsureNestedArrayItemType(payload, contentPropertyName, contentItemType);
        }

        var isKnownSuccessPayload = IsKnownSuccessPayload(payload, contentPropertyName);
        var normalized = !isKnownSuccessPayload && payload.TryGetProperty("error_code", out _)
            ? AddObjectType(payload, errorType)
            : AddObjectType(payload, successType);

        return contentItemType is null
            ? normalized
            : EnsureNestedArrayItemType(normalized, contentPropertyName, contentItemType);
    }

    private static bool IsKnownSuccessPayload(JsonElement payload, string contentPropertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        return payload.TryGetProperty(contentPropertyName, out _)
               || payload.TryGetProperty("stdout", out _)
               || payload.TryGetProperty("stderr", out _)
               || payload.TryGetProperty("return_code", out _)
               || payload.TryGetProperty("encrypted_stdout", out _)
               || payload.TryGetProperty("file_type", out _)
               || payload.TryGetProperty("is_file_update", out _)
               || payload.TryGetProperty("lines", out _)
               || payload.TryGetProperty("new_lines", out _)
               || payload.TryGetProperty("new_start", out _)
               || payload.TryGetProperty("old_lines", out _)
               || payload.TryGetProperty("old_start", out _)
               || payload.TryGetProperty("tool_references", out _)
               || payload.TryGetProperty("url", out _)
               || payload.TryGetProperty("retrieved_at", out _)
               || payload.TryGetProperty("text", out _)
               || payload.TryGetProperty("encrypted_content", out _);
    }

    private static JsonElement EnsureNestedArrayItemType(JsonElement payload, string propertyName, string itemType)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out var nested)
            || nested.ValueKind != JsonValueKind.Array)
        {
            return payload;
        }

        var changed = false;
        var normalizedItems = new List<object?>();

        foreach (var item in nested.EnumerateArray())
        {
            var normalizedItem = EnsureObjectType(item, itemType);
            changed |= normalizedItem.ValueKind != item.ValueKind || normalizedItem.GetRawText() != item.GetRawText();
            normalizedItems.Add(DeserializeToUntypedObject(normalizedItem));
        }

        if (!changed)
            return payload;

        var dictionary = DeserializeToDictionary(payload);
        dictionary[propertyName] = normalizedItems;
        return JsonSerializer.SerializeToElement(dictionary, Json);
    }

    private static JsonElement EnsureObjectType(JsonElement payload, string type)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return payload;

        if (payload.TryGetProperty("type", out var existingType)
            && existingType.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(existingType.GetString()))
        {
            return payload;
        }

        return AddObjectType(payload, type);
    }

    private static JsonElement AddObjectType(JsonElement payload, string type)
    {
        var dictionary = DeserializeToDictionary(payload);
        dictionary["type"] = type;
        return JsonSerializer.SerializeToElement(dictionary, Json);
    }

    private static Dictionary<string, object?> DeserializeToDictionary(JsonElement payload)
        => JsonSerializer.Deserialize<Dictionary<string, object?>>(payload.GetRawText(), Json) ?? [];

    private static object? DeserializeToUntypedObject(JsonElement payload)
        => JsonSerializer.Deserialize<object>(payload.GetRawText(), Json);

    private static bool ShouldUnwrapProviderExecutedContentProperty(string blockType, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("content", out _))
            return false;

        return blockType switch
        {
            "tool_result" => true,
            "mcp_tool_result" => true,
            "web_search_tool_result" => true,
            _ => false
        };
    }

    private static MessagesContent ToMessagesContentPreservingShape(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Array => new MessagesContent(
                JsonSerializer.Deserialize<List<MessageContentBlock>>(value.GetRawText(), Json) ?? []),
            JsonValueKind.String => new MessagesContent(value.GetString() ?? string.Empty),
            JsonValueKind.Null or JsonValueKind.Undefined => new MessagesContent(string.Empty),
            _ => new MessagesContent(value.Clone())
        };

    private static bool SupportsOuterIsErrorOnToolResultBlock(string blockType)
        => blockType is "tool_result" or "mcp_tool_result";

    private static string ResolveProviderExecutedInputBlockType(
        string resultBlockType,
        Dictionary<string, object>? providerMetadata)
    {
        if (providerMetadata is not null
            && providerMetadata.TryGetValue("server_name", out var serverName)
            && serverName is not null)
        {
            return "mcp_tool_use";
        }

        return resultBlockType switch
        {
            "mcp_tool_result" => "mcp_tool_use",
            _ => "server_tool_use"
        };
    }

    private static string ResolveProviderExecutedToolName(string resultBlockType)
        => resultBlockType switch
        {
            "web_search_tool_result" => "web_search",
            "web_fetch_tool_result" => "web_fetch",
            "advisor_tool_result" => "advisor",
            "code_execution_tool_result" => "code_execution",
            "bash_code_execution_tool_result" => "bash_code_execution",
            "text_editor_code_execution_tool_result" => "text_editor_code_execution",
            "tool_search_tool_result" => "tool_search_tool_regex",
            _ => "unknown"
        };

    private static bool SupportsTitleOnProviderExecutedInputBlock(string blockType)
        => !string.Equals(blockType, "server_tool_use", StringComparison.Ordinal);

    private static void ApplyProviderExecutedInputBlockMetadata(
        MessageContentBlock block,
        Dictionary<string, object>? providerMetadata)
    {
        if (providerMetadata is null || providerMetadata.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(block.Name)
            && providerMetadata.TryGetValue("tool_name", out var toolName)
            && toolName is not null)
        {
            block.Name = toolName.ToString();
        }

        if (string.IsNullOrWhiteSpace(block.Name)
            && providerMetadata.TryGetValue("name", out var name)
            && name is not null)
        {
            block.Name = name.ToString();
        }

        if (block.Caller is null
            && providerMetadata.TryGetValue("caller", out var caller)
            && caller is not null)
        {
            block.Caller = DeserializeFromObject<MessageCaller>(caller);
        }

        if (string.IsNullOrWhiteSpace(block.ServerName)
            && providerMetadata.TryGetValue("server_name", out var serverName)
            && serverName is not null)
        {
            block.ServerName = serverName.ToString();
        }
    }

    private static void ApplyProviderExecutedBlockMetadata(
        MessageContentBlock block,
        Dictionary<string, object>? providerMetadata)
    {
        if (providerMetadata is null || providerMetadata.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(block.ToolUseId)
            && providerMetadata.TryGetValue("tool_use_id", out var toolUseId)
            && toolUseId is not null)
        {
            block.ToolUseId = toolUseId.ToString();
        }

        if (string.IsNullOrWhiteSpace(block.ToolName)
            && providerMetadata.TryGetValue("tool_name", out var toolName)
            && toolName is not null)
        {
            block.ToolName = toolName.ToString();
        }

        if (string.IsNullOrWhiteSpace(block.Name)
            && providerMetadata.TryGetValue("name", out var name)
            && name is not null)
        {
            block.Name = name.ToString();
        }

        if (SupportsTitleOnProviderExecutedInputBlock(block.Type)
            && string.IsNullOrWhiteSpace(block.Title)
            && providerMetadata.TryGetValue("title", out var title)
            && title is not null)
        {
            block.Title = title.ToString();
        }

        if (block.Caller is null
            && providerMetadata.TryGetValue("caller", out var caller)
            && caller is not null)
        {
            block.Caller = DeserializeFromObject<MessageCaller>(caller);
        }

        if (string.IsNullOrWhiteSpace(block.ServerName)
            && providerMetadata.TryGetValue("server_name", out var serverName)
            && serverName is not null)
        {
            block.ServerName = serverName.ToString();
        }
    }

    private static bool TryGetProviderExecutedPayload(object? output, out JsonElement payload)
    {
        payload = default;

        if (output is null)
            return false;

        if (output is ModelContextProtocol.Protocol.CallToolResult callToolResult)
        {
            return TryGetProviderExecutedPayloadFromCallToolResult(callToolResult, out payload);
        }

        if (output is string text)
        {
            payload = JsonSerializer.SerializeToElement(text, Json);
            return true;
        }

        if (output is JsonElement outputJson)
        {
            if (outputJson.ValueKind == JsonValueKind.Object &&
                outputJson.TryGetProperty("structuredContent", out var inlineStructuredContent) &&
                inlineStructuredContent.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                payload = inlineStructuredContent.Clone();
                return true;
            }

            if (outputJson.ValueKind == JsonValueKind.Object
                && LooksLikeEmptyProviderExecutedCallToolResult(outputJson))
            {
                return false;
            }

            var deserialized = DeserializeFromObject<ModelContextProtocol.Protocol.CallToolResult>(outputJson);
            if (deserialized is not null
                && TryGetProviderExecutedPayloadFromCallToolResult(deserialized, out payload))
            {
                return true;
            }

            if (outputJson.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.String)
            {
                payload = outputJson.Clone();
                return true;
            }
        }

        var hydrated = DeserializeFromObject<ModelContextProtocol.Protocol.CallToolResult>(output);
        if (hydrated is not null
            && TryGetProviderExecutedPayloadFromCallToolResult(hydrated, out payload))
        {
            return true;
        }

        var serializedOutput = SerializeToNullableElement(output);
        if (serializedOutput is JsonElement serializedPayload)
        {
            payload = serializedPayload;
            return true;
        }

        return false;
    }

    private static bool TryGetProviderExecutedPayloadFromCallToolResult(
        ModelContextProtocol.Protocol.CallToolResult callToolResult,
        out JsonElement payload)
    {
        payload = default;

        var structuredContent = callToolResult.StructuredContent;
        if (structuredContent is JsonElement structuredJson
            && structuredJson.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            payload = structuredJson.Clone();
            return true;
        }

        if (callToolResult.Content is { Count: > 0 })
        {
            payload = JsonSerializer.SerializeToElement(callToolResult.Content, Json);
            return true;
        }

        if (callToolResult.IsError == true)
        {
            payload = JsonSerializer.SerializeToElement(callToolResult, Json);
            return true;
        }

        payload = default;
        return false;
    }

    private static bool LooksLikeEmptyProviderExecutedCallToolResult(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        if (!payload.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array
            || content.GetArrayLength() != 0)
        {
            return false;
        }

        if (payload.TryGetProperty("structuredContent", out var structuredContent)
            && structuredContent.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return false;
        }

        if (payload.TryGetProperty("isError", out var isError)
            && isError.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        return true;
    }
}
