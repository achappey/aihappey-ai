using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Messages.Mapping;

public static partial class MessagesUnifiedMapper
{
    private static IEnumerable<AIContentPart> ToUnifiedContentParts(MessagesContent content)
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
                        Metadata = CreateBlockMetadata(block)
                    };
                    break;
                case "thinking":
                case "redacted_thinking":
                    yield return new AIReasoningContentPart
                    {
                        Type = "reasoning",
                        Text = block.Thinking ?? block.Data,
                        Metadata = CreateBlockMetadata(block)
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
                        Metadata = CreateBlockMetadata(block)
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

    private static Dictionary<string, object?> CreateBlockMetadata(MessageContentBlock block)
        => new()
        {
            ["messages.block.type"] = block.Type,
            ["messages.block.raw"] = JsonSerializer.SerializeToElement(block, Json)
        };

    private static MessageContentBlock? ExtractRawBlock(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("messages.block.raw", out var raw) || raw is null)
            return null;

        return DeserializeFromObject<MessageContentBlock>(raw);
    }

    private static MessageContentBlock ToMessageFileBlock(AIFileContentPart file)
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
                    Type = file.Data?.ToString()?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? "url" : "base64",
                    Url = file.Data?.ToString()?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? file.Data?.ToString() : null,
                    Data = file.Data?.ToString()?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? null : file.Data?.ToString(),
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

            return new ModelContextProtocol.Protocol.CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    results = results
                }, Json)
            };
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
            yield break;

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
}
