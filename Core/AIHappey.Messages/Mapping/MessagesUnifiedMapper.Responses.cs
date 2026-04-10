using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Messages.Mapping;

public static partial class MessagesUnifiedMapper
{
    public static AIResponse ToUnifiedResponse(this MessagesResponse response, string providerId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var outputItems = ToUnifiedOutputItems(response).ToList();

        return new AIResponse
        {
            ProviderId = providerId,
            Model = response.Model,
            Status = ToUnifiedStatus(response.StopReason),
            Usage = response.Usage,
            Output = outputItems.Count == 0 ? null : new AIOutput { Items = outputItems },
            Metadata = BuildUnifiedResponseMetadata(response)
        };
    }

    public static MessagesResponse ToMessagesResponse(this AIResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var metadata = response.Metadata ?? new Dictionary<string, object?>();
        var role = ExtractValue<string>(metadata, "messages.response.role") ?? "assistant";

        return new MessagesResponse
        {
            Id = ExtractValue<string>(metadata, "messages.response.id") ?? $"msg_{Guid.NewGuid():N}",
            Container = ExtractObject<MessagesContainer>(metadata, "messages.response.container"),
            Content = ToMessageContentBlocks(response.Output).ToList(),
            Model = response.Model,
            Role = role,
            StopDetails = ExtractObject<MessagesStopDetails>(metadata, "messages.response.stop_details"),
            StopReason = ExtractValue<string>(metadata, "messages.response.stop_reason") ?? ToMessagesStopReason(response.Status),
            StopSequence = ExtractValue<string>(metadata, "messages.response.stop_sequence"),
            Type = ExtractValue<string>(metadata, "messages.response.type") ?? "message",
            Usage = ExtractObject<MessagesUsage>(metadata, "messages.response.usage") ?? DeserializeFromObject<MessagesUsage>(response.Usage),
            AdditionalProperties = ExtractObject<Dictionary<string, JsonElement>>(metadata, "messages.response.unmapped")
        };
    }

    private static Dictionary<string, object?> BuildUnifiedResponseMetadata(MessagesResponse response)
    {
        var raw = JsonSerializer.SerializeToElement(response, Json);
        var metadata = new Dictionary<string, object?>
        {
            ["messages.response.raw"] = raw,
            ["messages.response.id"] = response.Id,
            ["messages.response.container"] = response.Container,
            ["messages.response.role"] = response.Role,
            ["messages.response.stop_details"] = response.StopDetails,
            ["messages.response.stop_reason"] = response.StopReason,
            ["messages.response.stop_sequence"] = response.StopSequence,
            ["messages.response.type"] = response.Type,
            ["messages.response.usage"] = response.Usage
        };

        var unmapped = new Dictionary<string, JsonElement>();
        foreach (var prop in raw.EnumerateObject())
        {
            metadata[$"messages.response.{prop.Name}"] = prop.Value.Clone();

            if (prop.Name is not "id"
                and not "container"
                and not "content"
                and not "model"
                and not "role"
                and not "stop_details"
                and not "stop_reason"
                and not "stop_sequence"
                and not "type"
                and not "usage")
            {
                unmapped[prop.Name] = prop.Value.Clone();
            }
        }

        metadata["messages.response.unmapped"] = JsonSerializer.SerializeToElement(unmapped, Json);
        return metadata;
    }

    private static IEnumerable<AIOutputItem> ToUnifiedOutputItems(MessagesResponse response)
    {
        var messageContent = new List<AIContentPart>();

        foreach (var block in response.Content ?? [])
        {
            if (TryCreateUnifiedToolCallPart(block, out var toolPart))
            {
                yield return new AIOutputItem
                {
                    Type = "message",
                    Role = response.Role ?? "assistant",
                    Content = [toolPart],
                    Metadata = CreateBlockMetadata(block)
                };

                foreach (var sourceItem in ToSourceOutputItems(block))
                    yield return sourceItem;

                continue;
            }

            switch (block.Type)
            {
                case "text":
                    messageContent.Add(new AITextContentPart
                    {
                        Type = "text",
                        Text = block.Text ?? string.Empty,
                        Metadata = CreateBlockMetadata(block)
                    });
                    break;
                case "thinking":
                case "redacted_thinking":
                    messageContent.Add(new AIReasoningContentPart
                    {
                        Type = "reasoning",
                        Text = block.Thinking ?? block.Data,
                        Metadata = CreateBlockMetadata(block)
                    });
                    break;
                case "image":
                case "document":
                case "container_upload":
                    messageContent.Add(ToUnifiedFilePart(block));
                    break;
                default:
                    yield return new AIOutputItem
                    {
                        Type = block.Type,
                        Role = response.Role,
                        Content = ToUnifiedToolLikeContent(block),
                        Metadata = CreateBlockMetadata(block)
                    };
                    break;
            }

            foreach (var sourceItem in ToSourceOutputItems(block))
                yield return sourceItem;
        }

        if (messageContent.Count > 0)
        {
            yield return new AIOutputItem
            {
                Type = "message",
                Role = response.Role ?? "assistant",
                Content = messageContent,
                Metadata = new Dictionary<string, object?>
                {
                    ["messages.output.role"] = response.Role,
                    ["messages.output.raw_content"] = JsonSerializer.SerializeToElement(response.Content, Json)
                }
            };
        }
    }

    private static IEnumerable<MessageContentBlock> ToMessageContentBlocks(AIOutput? output)
    {
        foreach (var item in output?.Items ?? [])
        {
            if (string.Equals(item.Type, "source-url", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
            {
                var rawBlock = ExtractRawBlock(item.Metadata);
                if (rawBlock is not null)
                {
                    yield return rawBlock;
                    continue;
                }
            }

            foreach (var part in item.Content ?? [])
            {
                if (part is AIToolCallContentPart toolPart)
                {
                    foreach (var (assistantBlock, userBlock) in ToMessageToolBlocks(toolPart))
                    {
                        if (assistantBlock is not null)
                            yield return assistantBlock;

                        if (userBlock is not null)
                            yield return userBlock;
                    }

                    continue;
                }

                var rawBlock = ExtractRawBlock(part.Metadata);
                if (rawBlock is not null)
                {
                    yield return rawBlock;
                    continue;
                }

                switch (part)
                {
                    case AITextContentPart text:
                        yield return new MessageContentBlock { Type = "text", Text = text.Text };
                        break;
                    case AIReasoningContentPart reasoning:
                        yield return new MessageContentBlock { Type = "thinking", Thinking = reasoning.Text };
                        break;
                    case AIFileContentPart file:
                        yield return ToMessageFileBlock(file);
                        break;
                }
            }
        }
    }

    private static IEnumerable<AIEventEnvelope> CreateSourceEnvelopes(MessageContentBlock block, string? id)
    {
        foreach (var citation in block.Citations ?? [])
        {
            if (citation.Type == "web_search_result_location" && !string.IsNullOrWhiteSpace(citation.Url))
            {
                yield return CreateEnvelope("source-url", id, new AISourceUrlEventData
                {
                    SourceId = citation.EncryptedIndex ?? citation.Url,
                    Url = citation.Url,
                    Title = citation.Title ?? citation.Url,
                    Type = citation.Type
                });
            }
        }
    }

    private static AIFileContentPart ToUnifiedFilePart(MessageContentBlock block)
    {
        var source = block.Source;
        var data = source?.Url ?? source?.Data ?? source?.Content?.Text ?? (object?)JsonSerializer.SerializeToElement(block, Json);

        return new AIFileContentPart
        {
            Type = "file",
            MediaType = source?.MediaType ?? GuessMediaType(source?.Url),
            Filename = block.Title ?? block.FileId ?? block.Id,
            Data = data,
            Metadata = CreateBlockMetadata(block)
        };
    }

    private static List<AIContentPart>? ToUnifiedToolLikeContent(MessageContentBlock block)
    {
        if (block.Content is null)
            return null;

        return ToUnifiedContentParts(block.Content).ToList();
    }

    private static IEnumerable<AIOutputItem> ToSourceOutputItems(MessageContentBlock block)
    {
        foreach (var citation in block.Citations ?? [])
        {
            if (citation.Type != "web_search_result_location" || string.IsNullOrWhiteSpace(citation.Url))
                continue;

            yield return new AIOutputItem
            {
                Type = "source-url",
                Content =
                [
                    new AITextContentPart
                    {
                        Type = "text",
                        Text = citation.Title ?? citation.Url,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["messages.source.raw"] = JsonSerializer.SerializeToElement(citation, Json)
                        }
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    ["messages.source.url"] = citation.Url,
                    ["messages.source.title"] = citation.Title,
                    ["messages.source.type"] = citation.Type,
                    ["messages.source.raw"] = JsonSerializer.SerializeToElement(citation, Json)
                }
            };
        }
    }
}
