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
            Metadata = BuildUnifiedResponseMetadata(response),
            //  Events = null
        };
    }

    public static ResponseResult ToResponseResult(this AIResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var metadata = response.Metadata ?? new Dictionary<string, object?>();

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
            Metadata = ExtractObject<Dictionary<string, object?>>(metadata, "responses.metadata")
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

    private static Dictionary<string, object?> BuildUnifiedResponseMetadata(ResponseResult response)
        => new()
        {
            ["responses.id"] = response.Id,
            ["responses.object"] = response.Object,
            ["responses.created_at"] = response.CreatedAt,
            ["responses.completed_at"] = response.CompletedAt,
            ["responses.parallel_tool_calls"] = response.ParallelToolCalls,
            ["responses.temperature"] = response.Temperature,
            ["responses.text"] = response.Text,
            ["responses.tool_choice"] = response.ToolChoice,
            ["responses.tools"] = response.Tools?.ToList(),
            ["responses.reasoning"] = response.Reasoning,
            ["responses.store"] = response.Store,
            ["responses.max_output_tokens"] = response.MaxOutputTokens,
            ["responses.service_tier"] = response.ServiceTier,
            ["responses.error"] = response.Error,
            ["metadata"] = response.Metadata
        };
}
