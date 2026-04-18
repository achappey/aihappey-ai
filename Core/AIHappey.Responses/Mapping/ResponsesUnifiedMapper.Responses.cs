using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    public static AIResponse ToUnifiedResponse(this ResponseResult response, string providerId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var outputItems = ToUnifiedOutputItems(response, providerId).ToList();

        return new AIResponse
        {
            ProviderId = providerId,
            Model = response.Model,
            Status = response.Status,
            Usage = response.Usage,
            Output = outputItems.Count > 0 ? new AIOutput { Items = outputItems } : null,
            Metadata = response.Metadata,
        };
    }

    public static ResponseResult ToResponseResult(this AIResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var metadata = response.Metadata ?? [];

        return new ResponseResult
        {
            Id = ExtractValue<string>(metadata, "responses.id") ?? Guid.NewGuid().ToString("N"),
            Object = ExtractValue<string>(metadata, "responses.object") ?? "response",
            CreatedAt = ExtractValue<long?>(metadata, "responses.created_at") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CompletedAt = ExtractValue<long?>(metadata, "responses.completed_at"),
            Status = response.Status,
            ParallelToolCalls = ExtractValue<bool?>(metadata, "responses.parallel_tool_calls"),
            Model = response.Model ?? "unknown",
            Temperature = ExtractValue<float?>(metadata, "responses.temperature"),
            Usage = response.Usage,
            Output = ToResponseOutputObjects(response.Output, response.ProviderId).ToList(),
            Text = ExtractObject<object>(metadata, "responses.text"),
            ToolChoice = ExtractObject<object>(metadata, "responses.tool_choice"),
            Tools = ExtractObject<List<object>>(metadata, "responses.tools") ?? [],
            Reasoning = ExtractObject<object>(metadata, "responses.reasoning"),
            Store = ExtractValue<bool?>(metadata, "responses.store"),
            MaxOutputTokens = ExtractValue<int?>(metadata, "responses.max_output_tokens"),
            ServiceTier = ExtractValue<string>(metadata, "responses.service_tier"),
            Error = ExtractObject<ResponseResultError>(metadata, "responses.error"),
            Metadata = response.Metadata
        };
    }

    private static IEnumerable<AIOutputItem> ToUnifiedOutputItems(ResponseResult response, string providerId)
    {
        foreach (var item in response.Output ?? [])
        {
            if (item is null)
                continue;

            var map = ToJsonMap(item);
            var role = GetValue<string>(map, "role") ?? "assistant";
            var type = GetValue<string>(map, "type") ?? "message";

            if (TryCreateToolOutputItem(item, map, role, type, providerId, out var toolItem))
            {
                yield return toolItem;
                continue;
            }

            if (TryCreateImageOutputItem(item, map, role, type, out var imageItem))
            {
                yield return imageItem;
                continue;
            }

            if (TryCreateReasoningOutputItem(item, map, role, type, providerId, out var reasoningItem))
            {
                yield return reasoningItem;
                continue;
            }

            var content = new List<AIContentPart>();

            if (map.TryGetValue("content", out var contentObj))
            {
                if (contentObj is JsonElement contentJson && contentJson.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in contentJson.EnumerateArray())
                    {
                        var partType = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                        if (partType is "output_text" or "input_text")
                        {
                            content.Add(new AITextContentPart
                            {
                                Type = "text",
                                Text = part.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty,
                                Metadata = new Dictionary<string, object?> { ["responses.type"] = partType }
                            });
                        }
                        else
                        {
                            content.Add(new AITextContentPart
                            {
                                Type = "text",
                                Text = part.GetRawText(),
                                Metadata = new Dictionary<string, object?> { ["responses.raw_content_part"] = true }
                            });
                        }
                    }
                }
            }

            if (content.Count == 0 && map.TryGetValue("text", out var textObj))
            {
                var text = textObj is JsonElement e ? e.ToString() : textObj?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    content.Add(new AITextContentPart { Type = "text", Text = text! });
            }

            yield return new AIOutputItem
            {
                Type = type,
                Role = role,
                Content = content.Count > 0 ? content : null,
                Metadata = new Dictionary<string, object?>
                {
                    ["responses.raw_output"] = item
                }
            };
        }

        if (response.Output?.Any() != true && response.Text is not null)
        {
            yield return new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content = [new AITextContentPart { Type = "text", Text = response.Text.ToString() ?? string.Empty }]
            };
        }
    }

    private static bool TryCreateReasoningOutputItem(
        object rawItem,
        Dictionary<string, object?> map,
        string role,
        string type,
        string providerId,
        out AIOutputItem item)
    {
        if (!string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            item = null!;
            return false;
        }

        var reasoningId = GetValue<string>(map, "id");
        var encryptedContent = GetValue<object>(map, "encrypted_content");
        var status = GetValue<string>(map, "status");
        var summary = ExtractReasoningSummaryParts(map);
        var content = new List<AIContentPart>();

        if (map.TryGetValue("content", out var contentObj)
            && contentObj is JsonElement contentJson
            && contentJson.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in contentJson.EnumerateArray())
            {
                var partType = part.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                var text = part.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty;

                if (partType is "reasoning_text" or "summary_text")
                {
                    content.Add(new AIReasoningContentPart
                    {
                        Type = "reasoning",
                        Text = text,
                        Metadata = CreateReasoningContentMetadata(providerId, reasoningId, encryptedContent, summary, partType)
                    });
                    continue;
                }

                content.Add(new AITextContentPart
                {
                    Type = "text",
                    Text = part.GetRawText(),
                    Metadata = new Dictionary<string, object?> { ["responses.raw_content_part"] = true }
                });
            }
        }

        if (content.Count == 0)
        {
            foreach (var summaryPart in summary)
            {
                content.Add(new AIReasoningContentPart
                {
                    Type = "reasoning",
                    Text = summaryPart.Text,
                    Metadata = CreateReasoningContentMetadata(providerId, reasoningId, encryptedContent, summary, summaryPart.Type)
                });
            }
        }

        if (content.Count == 0 && HasMeaningfulValue(encryptedContent))
        {
            content.Add(new AIReasoningContentPart
            {
                Type = "reasoning",
                Text = string.Empty,
                Metadata = CreateReasoningContentMetadata(providerId, reasoningId, encryptedContent, summary, "reasoning")
            });
        }

        item = new AIOutputItem
        {
            Type = type,
            Role = role,
            Content = content.Count > 0 ? content : null,
            Metadata = CreateReasoningItemMetadata(providerId, reasoningId, encryptedContent, summary, status, rawItem)
        };

        return true;
    }

    private static List<ResponseReasoningSummaryTextPart> ExtractReasoningSummaryParts(Dictionary<string, object?> map)
    {
        if (!map.TryGetValue("summary", out var summaryObj)
            || summaryObj is not JsonElement summaryJson
            || summaryJson.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var summary = new List<ResponseReasoningSummaryTextPart>();
        foreach (var part in summaryJson.EnumerateArray())
        {
            var type = part.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            var text = part.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;

            if (string.IsNullOrWhiteSpace(text))
                continue;

            summary.Add(new ResponseReasoningSummaryTextPart
            {
                Type = string.IsNullOrWhiteSpace(type) ? "summary_text" : type!,
                Text = text
            });
        }

        return summary;
    }

    private static Dictionary<string, object?> CreateReasoningItemMetadata(
        string providerId,
        string? reasoningId,
        object? encryptedContent,
        IReadOnlyCollection<ResponseReasoningSummaryTextPart> summary,
        string? status,
        object rawItem)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["responses.type"] = "reasoning",
            ["responses.raw_output"] = rawItem
        };

        if (!string.IsNullOrWhiteSpace(reasoningId))
            metadata["id"] = reasoningId;

        if (!string.IsNullOrWhiteSpace(status))
            metadata["status"] = status;

        MergeProviderScopedReasoningItemIdMetadata(metadata, providerId, reasoningId);
        MergeProviderScopedEncryptedContentMetadata(metadata, providerId, encryptedContent);
        MergeProviderScopedReasoningSummaryMetadata(metadata, providerId, summary);
        return metadata;
    }

    private static Dictionary<string, object?> CreateReasoningContentMetadata(
        string providerId,
        string? reasoningId,
        object? encryptedContent,
        IReadOnlyCollection<ResponseReasoningSummaryTextPart> summary,
        string? partType)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["responses.type"] = partType ?? "reasoning"
        };

        if (!string.IsNullOrWhiteSpace(reasoningId))
            metadata["id"] = reasoningId;

        MergeProviderScopedReasoningItemIdMetadata(metadata, providerId, reasoningId);
        MergeProviderScopedEncryptedContentMetadata(metadata, providerId, encryptedContent);
        MergeProviderScopedReasoningSummaryMetadata(metadata, providerId, summary);
        return metadata;
    }

    private static void MergeProviderScopedReasoningSummaryMetadata(
        Dictionary<string, object?> metadata,
        string providerId,
        IReadOnlyCollection<ResponseReasoningSummaryTextPart>? summary)
    {
        if (summary is not { Count: > 0 })
            return;

        var providerMetadata = GetOrCreateProviderScopedMetadata(metadata, providerId);
        providerMetadata["summary"] = JsonSerializer.SerializeToElement(summary, Json);
        metadata[providerId] = providerMetadata;
    }

    private static bool TryCreateImageOutputItem(
        object rawItem,
        Dictionary<string, object?> map,
        string role,
        string type,
        out AIOutputItem item)
    {
        if (!string.Equals(type, "image_generation_call", StringComparison.OrdinalIgnoreCase))
        {
            item = null!;
            return false;
        }

        var result = GetValue<string>(map, "result");
        if (string.IsNullOrWhiteSpace(result))
        {
            item = null!;
            return false;
        }

        var mediaType = NormalizeImageMediaType(GetValue<string>(map, "output_format"))
            ?? GuessMediaType(result)
            ?? "image/png";

        item = new AIOutputItem
        {
            Type = "message",
            Role = role,
            Content =
            [
                new AIFileContentPart
                {
                    Type = "file",
                    MediaType = mediaType,
                    Data = result,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["responses.type"] = type
                    }
                }
            ],
            Metadata = new Dictionary<string, object?>
            {
                ["responses.raw_output"] = rawItem
            }
        };

        return true;
    }

    private static IEnumerable<object> ToResponseOutputObjects(AIOutput? output, string providerId)
    {
        foreach (var item in output?.Items ?? [])
        {
            var toolParts = (item.Content ?? []).OfType<AIToolCallContentPart>().ToList();
            var nonToolParts = (item.Content ?? []).Where(a => a is not AIToolCallContentPart).ToList();

            var compactionToolPart = toolParts.FirstOrDefault(IsCompactionToolCall);
            if (compactionToolPart is not null)
            {
                var encryptedContent = compactionToolPart.Metadata is not null
                    ? ExtractNestedValue<string>(compactionToolPart.Metadata, providerId, "encrypted_content")
                    : null;

                encryptedContent ??= item.Metadata is not null
                    ? ExtractNestedValue<string>(item.Metadata, providerId, "encrypted_content")
                    : null;

                if (!string.IsNullOrWhiteSpace(encryptedContent))
                {
                    yield return new ResponseCompactionItem
                    {
                        Id = ExtractValue<string>(item.Metadata, "id")
                             ?? ExtractValue<string>(compactionToolPart.Metadata, "id")
                             ?? compactionToolPart.ToolCallId,
                        EncryptedContent = encryptedContent
                    };
                    continue;
                }
            }

            var content = new List<object>();
            foreach (var part in nonToolParts)
            {
                switch (part)
                {
                    case AITextContentPart text:
                        content.Add(new OutputTextPart(text.Text));
                        break;
                    case AIFileContentPart file:
                        content.Add(new InputFilePart
                        {
                            Filename = file.Filename,
                            FileData = file.Data?.ToString()
                        });
                        break;
                }
            }

            if (content.Count > 0 || toolParts.Count == 0)
            {
                yield return new
                {
                    type = item.Type,
                    role = item.Role,
                    content
                };
            }

            foreach (var toolPart in toolParts.Where(a => a.IsClientToolCall))
            {
                yield return new
                {
                    type = "function_call",
                    id = ExtractValue<string>(toolPart.Metadata, "id"),
                    call_id = toolPart.ToolCallId,
                    name = toolPart.ToolName ?? toolPart.Title ?? "tool",
                    arguments = SerializePayload(toolPart.Input, "{}"),
                    status = toolPart.State
                };

                if (HasToolOutput(toolPart))
                {
                    yield return new
                    {
                        type = "function_call_output",
                        id = ExtractValue<string>(toolPart.Metadata, "id"),
                        call_id = toolPart.ToolCallId,
                        output = SerializePayload(CreateToolOutputValue(toolPart), "{}"),
                        status = toolPart.State
                    };
                }
            }
        }
    }

    private static ResponseFunctionCallItem CreateResponseFunctionCallItem(
        AIToolCallContentPart toolPart,
        Dictionary<string, object?> metadata)
        => new()
        {
            Id = ExtractValue<string>(toolPart.Metadata, "id") ?? ExtractValue<string>(metadata, "id"),
            CallId = toolPart.ToolCallId,
            Name = toolPart.ToolName ?? toolPart.Title ?? ExtractValue<string>(metadata, "name") ?? "unknown",
            Arguments = SerializePayload(toolPart.Input, "{}"),
            Status = NormalizeResponsesToolStatus(toolPart.State ?? ExtractValue<string>(metadata, "status"), hasOutput: HasToolOutput(toolPart))
        };

    private static ResponseFunctionCallOutputItem CreateResponseFunctionCallOutputItem(
        AIToolCallContentPart toolPart,
        Dictionary<string, object?> metadata)
        => new()
        {
            Id = ExtractValue<string>(toolPart.Metadata, "id") ?? ExtractValue<string>(metadata, "id"),
            CallId = toolPart.ToolCallId,
            Output = SerializePayload(CreateToolOutputValue(toolPart), "{}"),
            Status = NormalizeResponsesToolStatus(toolPart.State ?? ExtractValue<string>(metadata, "status"), hasOutput: true)
        };

    private static bool TryCreateToolOutputItem(
        object rawItem,
        Dictionary<string, object?> map,
        string role,
        string type,
        string providerId,
        out AIOutputItem item)
    {
        var toolPart = type switch
        {
            "compaction" => CreateUnifiedCompactionToolPart(
                providerId,
                GetValue<string>(map, "id"),
                GetValue<object>(map, "encrypted_content"),
                rawItem),
            "function_call" => new AIToolCallContentPart
            {
                Type = type,
                ToolCallId = GetValue<string>(map, "call_id") ?? GetValue<string>(map, "id") ?? Guid.NewGuid().ToString("N"),
                ToolName = GetValue<string>(map, "name"),
                Title = GetValue<string>(map, "name"),
                Input = ParseJsonString(GetValue<string>(map, "arguments")),
                State = GetValue<string>(map, "status"),
                ProviderExecuted = false,
                Metadata = new Dictionary<string, object?>
                {
                    ["responses.type"] = type,
                    ["responses.raw_output"] = rawItem
                }
            },
            "custom_tool_call" => new AIToolCallContentPart
            {
                Type = type,
                ToolCallId = GetValue<string>(map, "call_id") ?? GetValue<string>(map, "id") ?? Guid.NewGuid().ToString("N"),
                ToolName = GetValue<string>(map, "name") ?? type,
                Title = GetValue<string>(map, "name"),
                Input = GetValue<object>(map, "input") ?? ParseJsonString(GetValue<string>(map, "arguments")),
                State = GetValue<string>(map, "status"),
                ProviderExecuted = true,
                Metadata = new Dictionary<string, object?>
                {
                    ["responses.type"] = type,
                    ["responses.raw_output"] = rawItem
                }
            },
            "mcp_call" => new AIToolCallContentPart
            {
                Type = type,
                ToolCallId = GetValue<string>(map, "call_id") ?? GetValue<string>(map, "id") ?? Guid.NewGuid().ToString("N"),
                ToolName = GetValue<string>(map, "name") ?? type,
                Title = GetValue<string>(map, "name"),
                Input = GetValue<object>(map, "arguments") ?? GetValue<object>(map, "input"),
                Output = GetValue<object>(map, "output"),
                State = GetValue<string>(map, "status"),
                ProviderExecuted = true,
                Metadata = new Dictionary<string, object?>
                {
                    ["responses.type"] = type,
                    ["responses.raw_output"] = rawItem
                }
            },
            "code_interpreter_call" => new AIToolCallContentPart
            {
                Type = type,
                ToolCallId = GetValue<string>(map, "call_id") ?? GetValue<string>(map, "id") ?? Guid.NewGuid().ToString("N"),
                ToolName = "code_interpreter",
                Title = "code_interpreter",
                Input = GetValue<object>(map, "code"),
                Output = GetValue<object>(map, "outputs"),
                State = GetValue<string>(map, "status"),
                ProviderExecuted = true,
                Metadata = new Dictionary<string, object?>
                {
                    ["responses.type"] = type,
                    ["responses.raw_output"] = rawItem
                }
            },
            _ => null
        };

        if (toolPart is null)
        {
            item = null!;
            return false;
        }

        item = new AIOutputItem
        {
            Type = "message",
            Role = role,
            Content = [toolPart],
            Metadata = string.Equals(type, "compaction", StringComparison.OrdinalIgnoreCase)
                ? CreateCompactionMessageMetadata(
                    providerId,
                    GetValue<string>(map, "id"),
                    GetValue<object>(map, "encrypted_content"),
                    rawItem)
                : new Dictionary<string, object?>
                {
                    ["responses.raw_output"] = rawItem
                }
        };

        return true;
    }

    private static bool HasToolOutput(AIToolCallContentPart toolPart)
        => toolPart.Output is not null;

    private static object? CreateToolOutputValue(AIToolCallContentPart toolPart)
        => toolPart.Output;

    private static string? NormalizeResponsesToolStatus(string? state, bool hasOutput)
    {
        var normalized = state?.Trim().ToLowerInvariant();

        return normalized switch
        {
            null or "" => hasOutput ? "completed" : null,
            "completed" or "in_progress" or "incomplete" => normalized,
            "approval-responded" or "output-available" or "output-error" => "completed",
            "input-available" or "approval-requested" => "in_progress",
            _ => hasOutput ? "completed" : "in_progress"
        };
    }

    private static string? NormalizeImageMediaType(string? outputFormat)
        => outputFormat?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            var value when value.StartsWith("image/", StringComparison.Ordinal) => value,
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "webp" => "image/webp",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            _ => $"image/{outputFormat.Trim().ToLowerInvariant()}"
        };
}
