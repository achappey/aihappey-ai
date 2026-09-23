using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.DevicAI;

public partial class DevicAIProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = ParseDevicAIRoute(request.Model);
        return route.Kind switch
        {
            DevicAIRouteKind.Agent => CreateAgentResponse(await RunAgentAsync(request, route, cancellationToken)),
            DevicAIRouteKind.Assistant => CreateAssistantResponse(await RunAssistantAsync(request, route, cancellationToken)),
            _ => throw new NotSupportedException("The DevicAI Whisper model is available through transcription endpoints, not language generation endpoints.")
        };
    }

    private AIResponse CreateAgentResponse(DevicAIAgentExecution execution)
    {
        var content = new List<AIContentPart>();
        if (execution.Created)
            content.Add(CreateThreadIdentityPart(execution));

        var messages = GetArray(execution.Thread, "messages");
        foreach (var message in messages.Skip(execution.BaselineMessageCount))
        {
            var role = GetString(message, "role");
            var text = GetString(message, "content");
            if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(text))
            {
                content.Add(new AITextContentPart
                {
                    Type = "text",
                    Text = text,
                    Metadata = new Dictionary<string, object?> { ["devicai.raw"] = message.Clone() }
                });
            }
            else if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                var callId = GetString(message, "tool_call_id") ?? $"devicai-agent-tool-{Guid.NewGuid():N}";
                content.Add(new AIToolCallContentPart
                {
                    Type = "tool-call",
                    ToolCallId = callId,
                    ToolName = NormalizeToolName(GetString(message, "name") ?? "devicai_agent_tool"),
                    Title = GetString(message, "name"),
                    Input = new { },
                    Output = new CallToolResult
                    {
                        Content = [],
                        StructuredContent = JsonSerializer.SerializeToElement(new
                        {
                            message = text,
                            raw = message.Clone()
                        }, DevicAIJson)
                    },
                    ProviderExecuted = true,
                    State = "output-available",
                    Metadata = new Dictionary<string, object?> { ["devicai.raw"] = message.Clone() }
                });
            }
        }

        if (string.Equals(GetString(execution.Thread, "status"), "AWAITING_APPROVAL", StringComparison.OrdinalIgnoreCase))
            content.Add(CreateThreadApprovalPart(execution));

        var metadata = CreateAgentMetadata(execution);
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = execution.Route.ModelId,
            Status = NormalizeResponseStatus(GetString(execution.Thread, "status")),
            Output = content.Count == 0 ? null : new AIOutput
            {
                Items = [new AIOutputItem { Role = "assistant", Content = content, Metadata = metadata }],
                Metadata = metadata
            },
            Usage = CreateUsage(execution.Thread),
            Metadata = metadata
        };
    }

    private AIResponse CreateAssistantResponse(DevicAIAssistantExecution execution)
    {
        var content = new List<AIContentPart>();
        if (execution.Created)
            content.Add(CreateChatIdentityPart(execution));
        content.AddRange(MapAssistantMessages(execution.Messages));

        var metadata = CreateAssistantMetadata(execution);
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = execution.Route.ModelId,
            Status = NormalizeResponseStatus(execution.Status),
            Output = content.Count == 0 ? null : new AIOutput
            {
                Items = [new AIOutputItem { Role = "assistant", Content = content, Metadata = metadata }],
                Metadata = metadata
            },
            Usage = execution.History.HasValue ? CreateUsage(execution.History.Value) : null,
            Metadata = metadata
        };
    }

    private IEnumerable<AIContentPart> MapAssistantMessages(IReadOnlyList<JsonElement> messages)
    {
        var toolOutputs = messages
            .Where(message => string.Equals(GetString(message, "role"), "tool", StringComparison.OrdinalIgnoreCase))
            .Select(message => (Id: GetString(message, "tool_call_id"), Message: message))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
            .GroupBy(entry => entry.Id!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Message, StringComparer.Ordinal);

        foreach (var message in messages)
        {
            if (!string.Equals(GetString(message, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryGetProperty(message, "content", out var body) && body.ValueKind == JsonValueKind.Object)
            {
                var text = GetString(body, "message");
                if (!string.IsNullOrEmpty(text))
                    yield return new AITextContentPart
                    {
                        Type = "text",
                        Text = text,
                        Metadata = CreateMessageMetadata(message, body)
                    };
                else if (TryGetProperty(body, "data", out var data)
                         && data.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    yield return new AITextContentPart
                    {
                        Type = "text",
                        Text = data.GetRawText(),
                        Metadata = CreateMessageMetadata(message, body)
                    };

                if (TryGetProperty(body, "files", out var files) && files.ValueKind == JsonValueKind.Array)
                    foreach (var file in files.EnumerateArray())
                    {
                        var url = GetString(file, "donwloadUrl", "downloadUrl");
                        if (string.IsNullOrWhiteSpace(url)) continue;
                        yield return new AIFileContentPart
                        {
                            Type = "file",
                            Filename = GetString(file, "name"),
                            MediaType = DevicAIFileTypeToMediaType(GetString(file, "fileType")),
                            Data = url,
                            Metadata = new Dictionary<string, object?> { ["devicai.raw"] = file.Clone() }
                        };
                    }
            }

            foreach (var call in GetArray(message, "tool_calls"))
            {
                var callId = GetString(call, "id") ?? $"devicai-call-{Guid.NewGuid():N}";
                var function = TryGetProperty(call, "function", out var functionValue)
                    ? functionValue
                    : default;
                var name = function.ValueKind == JsonValueKind.Object
                    ? GetString(function, "name") ?? "devicai_tool"
                    : "devicai_tool";
                var input = function.ValueKind == JsonValueKind.Object
                    ? ParseJsonValue(GetString(function, "arguments"))
                    : JsonSerializer.SerializeToElement(new { }, DevicAIJson);
                var providerExecuted = toolOutputs.TryGetValue(callId, out var toolOutput);
                yield return new AIToolCallContentPart
                {
                    Type = "tool-call",
                    ToolCallId = callId,
                    ToolName = NormalizeToolName(name),
                    Title = name,
                    Input = input,
                    Output = providerExecuted
                        ? new CallToolResult
                        {
                            Content = [],
                            StructuredContent = JsonSerializer.SerializeToElement(new
                            {
                                message = TryGetProperty(toolOutput, "content", out var outputBody)
                                    ? GetString(outputBody, "message")
                                    : null,
                                raw = toolOutput.Clone()
                            }, DevicAIJson)
                        }
                        : null,
                    ProviderExecuted = providerExecuted,
                    State = providerExecuted ? "output-available" : "input-available",
                    Metadata = new Dictionary<string, object?> { ["devicai.raw"] = call.Clone() }
                };
            }
        }
    }

    private AIToolCallContentPart CreateThreadIdentityPart(DevicAIAgentExecution execution)
    {
        var threadId = GetString(execution.Thread, "id")!;
        return new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = $"devicai-create-thread-{threadId}",
            ToolName = CreateThreadToolName,
            Title = "Create DevicAI thread",
            Input = new { agentId = execution.Route.Target, agent_id = execution.Route.Target },
            Output = CreateThreadIdentityResult(execution),
            ProviderExecuted = true,
            State = "output-available",
            Metadata = CreateAgentMetadata(execution)
        };
    }

    private static AIToolCallContentPart CreateThreadApprovalPart(DevicAIAgentExecution execution)
    {
        var threadId = GetString(execution.Thread, "id")!;
        return new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = $"devicai-approve-thread-{threadId}",
            ToolName = ApproveThreadToolName,
            Title = "Approve DevicAI thread",
            Input = new { threadId, thread_id = threadId },
            ProviderExecuted = true,
            State = "approval-requested",
            Approval = new AIToolCallApproval { Approved = false, Id = threadId },
            Metadata = new Dictionary<string, object?>
            {
                ["threadId"] = threadId,
                ["thread_id"] = threadId,
                ["devicai.raw"] = execution.Thread.Clone()
            }
        };
    }

    private AIToolCallContentPart CreateChatIdentityPart(DevicAIAssistantExecution execution)
        => new()
        {
            Type = "tool-call",
            ToolCallId = $"devicai-create-chat-{execution.ChatUid}",
            ToolName = CreateChatToolName,
            Title = "Create DevicAI chat",
            Input = new { identifier = execution.Route.Target },
            Output = CreateChatIdentityResult(execution),
            ProviderExecuted = true,
            State = "output-available",
            Metadata = CreateAssistantMetadata(execution)
        };

    private static CallToolResult CreateThreadIdentityResult(DevicAIAgentExecution execution)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                type = CreateThreadToolName,
                threadId = GetString(execution.Thread, "id"),
                thread_id = GetString(execution.Thread, "id"),
                agentId = execution.Route.Target,
                agent_id = execution.Route.Target,
                status = GetString(execution.Thread, "status"),
                thread = execution.Thread.Clone()
            }, DevicAIJson)
        };

    private static CallToolResult CreateChatIdentityResult(DevicAIAssistantExecution execution)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                type = CreateChatToolName,
                chatUid = execution.ChatUid,
                chatUID = execution.ChatUid,
                chat_uid = execution.ChatUid,
                identifier = execution.Route.Target,
                raw = execution.Raw.Clone()
            }, DevicAIJson)
        };

    private Dictionary<string, object?> CreateAgentMetadata(DevicAIAgentExecution execution)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["devicai.thread_id"] = GetString(execution.Thread, "id"),
            ["devicai.agent_id"] = execution.Route.Target,
            ["devicai.status"] = GetString(execution.Thread, "status"),
            ["devicai.cost"] = GetDecimal(execution.Thread, "cost"),
            ["devicai.raw"] = execution.Thread.Clone(),
            ["threadId"] = GetString(execution.Thread, "id"),
            ["thread_id"] = GetString(execution.Thread, "id")
        };

    private Dictionary<string, object?> CreateAssistantMetadata(DevicAIAssistantExecution execution)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["devicai.chat_uid"] = execution.ChatUid,
            ["devicai.assistant_identifier"] = execution.Route.Target,
            ["devicai.status"] = execution.Status,
            ["devicai.raw"] = execution.Raw.Clone(),
            ["devicai.history"] = execution.History?.Clone(),
            ["devicai.uploads"] = execution.Uploads,
            ["chatUid"] = execution.ChatUid,
            ["chatUID"] = execution.ChatUid,
            ["chat_uid"] = execution.ChatUid
        };

    private static Dictionary<string, object?> CreateMessageMetadata(JsonElement message, JsonElement body)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["devicai.raw"] = message.Clone(),
            ["devicai.summary"] = GetString(message, "summary"),
            ["devicai.content_source"] = GetString(message, "contentSource")
        };
        if (TryGetProperty(body, "data", out var data)
            && data.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            metadata["devicai.data"] = data.Clone();
        return metadata;
    }

    private static AIUsage? CreateUsage(JsonElement raw)
    {
        var usage = TryGetProperty(raw, "tokenUsage", out var tokenUsage)
                    && tokenUsage.ValueKind == JsonValueKind.Object
            ? tokenUsage
            : raw;
        var input = GetInt(usage, "inputTokens", "input_tokens");
        var output = GetInt(usage, "outputTokens", "output_tokens");
        var total = GetInt(usage, "totalTokens", "total_tokens") ?? AddTokens(input, output);
        if (!input.HasValue && !output.HasValue && !total.HasValue)
            return null;
        return new AIUsage
        {
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = total,
            CachedInputTokens = GetInt(usage, "inputCachedTokens", "input_cached_tokens"),
            CacheWriteInputTokens = GetInt(usage, "inputCacheWriteTokens", "input_cache_write_tokens"),
            ReasoningTokens = GetInt(usage, "reasoningOutputTokens", "reasoning_output_tokens"),
            AdditionalProperties = new Dictionary<string, JsonElement> { ["devicai"] = usage.Clone() }
        };
    }

    private static JsonElement ParseJsonValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return JsonSerializer.SerializeToElement(new { }, DevicAIJson);
        try { return JsonSerializer.Deserialize<JsonElement>(value, DevicAIJson).Clone(); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(value, DevicAIJson); }
    }

    private static string NormalizeToolName(string value)
        => new string(value.Select(character => char.IsLetterOrDigit(character)
            ? char.ToLowerInvariant(character)
            : '_').ToArray()).Trim('_');

    private static string NormalizeResponseStatus(string? status)
        => status?.ToUpperInvariant() switch
        {
            "FAILED" or "ERROR" or "LIMIT_EXCEEDED" => "failed",
            "RUNNING" or "PROCESSING" => "in_progress",
            "AWAITING_APPROVAL" or "WAITING_FOR_TOOL_RESPONSE" or "PAUSED" => "incomplete",
            _ => "completed"
        };

    private static string? DevicAIFileTypeToMediaType(string? fileType)
        => fileType?.ToUpperInvariant() switch
        {
            "IMAGE" => "image/*",
            "DOCUMENT" => "application/octet-stream",
            "VIDEO" => "video/*",
            "AUDIO" => "audio/*",
            _ => null
        };
}
