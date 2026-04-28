using System.Text;
using System.Text.Json;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static readonly AsyncLocal<ResponseReverseStreamState?> CurrentReverseStreamState = new();

    private static ResponseReverseStreamState GetReverseStreamState()
        => CurrentReverseStreamState.Value ??= new ResponseReverseStreamState();

    private static void ClearReverseStreamState()
        => CurrentReverseStreamState.Value = null;

    private static int ResolveReverseSequenceNumber(Dictionary<string, object?> data)
    {
        var sequenceNumber = GetValue<int?>(data, "sequence_number");
        if (sequenceNumber is > 0)
            return sequenceNumber.Value;

        var state = GetReverseStreamState();
        return state.NextSequenceNumber++;
    }

    private static ResponseReverseItemState GetOrCreateReverseItemState(string? id, string defaultItemType)
    {
        var state = GetReverseStreamState();
        var itemId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;

        if (!state.ItemsById.TryGetValue(itemId, out var itemState))
        {
            itemState = new ResponseReverseItemState
            {
                ItemId = itemId,
                ItemType = defaultItemType,
                OutputIndex = state.NextOutputIndex++
            };

            state.ItemsById[itemId] = itemState;
        }
        else if (string.IsNullOrWhiteSpace(itemState.ItemType))
        {
            itemState.ItemType = defaultItemType;
        }

        return itemState;
    }

    private static void UpdateReverseItemState(
        ResponseReverseItemState itemState,
        string? itemType = null,
        string? toolName = null,
        string? title = null,
        bool? providerExecuted = null,
        Dictionary<string, Dictionary<string, object>>? providerMetadata = null)
    {
        if (!string.IsNullOrWhiteSpace(itemType))
            itemState.ItemType = itemType;

        if (!string.IsNullOrWhiteSpace(toolName))
            itemState.ToolName = toolName;

        if (!string.IsNullOrWhiteSpace(title))
            itemState.Title = title;

        if (providerExecuted is not null)
            itemState.ProviderExecuted = providerExecuted;

        if (providerMetadata is not null)
            itemState.ProviderMetadata = providerMetadata;
    }

    private static string ResolveToolItemType(AIToolInputStartEventData toolInputStart)
    {
        if (toolInputStart.ProviderExecuted != true)
        {
            return string.Equals(toolInputStart.ToolName, "mcp_call", StringComparison.OrdinalIgnoreCase)
                ? "mcp_call"
                : "function_call";
        }

        return toolInputStart.ToolName switch
        {
            "web_search" => "web_search_call",
            "file_search" => "file_search_call",
            "mcp_call" => "mcp_call",
            "code_interpreter" => "code_interpreter_call",
            "custom_tool" => "custom_tool_call",
            _ => "custom_tool_call"
        };
    }

    private static Dictionary<string, JsonElement>? ToJsonElementDictionary(Dictionary<string, object?>? value)
    {
        if (value is null)
            return null;

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var entry in value)
        {
            if (entry.Value is null)
                continue;

            result[entry.Key] = JsonSerializer.SerializeToElement(CloneIfJsonElement(entry.Value), Json);
        }

        return result.Count == 0 ? null : result;
    }

    private static ResponseStreamContentPart CreateResponseContentPart(
        string type,
        string? text = null,
        Dictionary<string, object?>? additionalProperties = null)
        => new()
        {
            Type = type,
            Text = text,
            AdditionalProperties = ToJsonElementDictionary(additionalProperties)
        };

    private static ResponseStreamAnnotation CreateResponseAnnotation(
        string type,
        Dictionary<string, object?>? additionalProperties = null)
        => new()
        {
            Type = type,
            AdditionalProperties = ToJsonElementDictionary(additionalProperties)
        };

    private static ResponseStreamItem CreateResponseStreamItem(
        ResponseReverseItemState itemState,
        string? status = null,
        IReadOnlyList<ResponseStreamContentPart>? content = null,
        Dictionary<string, object?>? additionalProperties = null)
    {
        additionalProperties ??= [];

        if (itemState.ProviderExecuted is not null)
            additionalProperties["provider_executed"] = itemState.ProviderExecuted;

        if (itemState.ProviderMetadata is not null)
            additionalProperties["provider_metadata"] = itemState.ProviderMetadata;

        if (!string.IsNullOrWhiteSpace(itemState.Signature))
        {
            if (string.Equals(itemState.ItemType, "reasoning", StringComparison.OrdinalIgnoreCase))
                additionalProperties["encrypted_content"] = itemState.Signature;
        }

        if (itemState.Input is not null)
            additionalProperties["input"] = CloneIfJsonElement(itemState.Input);

        if (itemState.Output is not null)
            additionalProperties["output"] = CloneIfJsonElement(itemState.Output);

        return new ResponseStreamItem
        {
            Id = itemState.ItemId,
            Type = itemState.ItemType,
            Status = status,
            Role = string.Equals(itemState.ItemType, "message", StringComparison.OrdinalIgnoreCase) ? "assistant" : null,
            Name = itemState.ToolName ?? itemState.Title,
            Arguments = itemState.ItemType is "function_call" or "mcp_call"
                ? itemState.SerializedInput
                : null,
            Content = content,
            AdditionalProperties = ToJsonElementDictionary(additionalProperties)
        };
    }

    private static string? ExtractReasoningSignature(
        Dictionary<string, Dictionary<string, object>>? providerMetadata,
        Dictionary<string, object?> data)
    {
        var signature = GetValue<string>(data, "signature");
        if (!string.IsNullOrWhiteSpace(signature))
            return signature;

        if (providerMetadata is null)
            return null;

        foreach (var scoped in providerMetadata.Values)
        {
            if (scoped.TryGetValue("signature", out var value)
                && !string.IsNullOrWhiteSpace(value?.ToString()))
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static string SerializeToolInput(object? input)
        => SerializePayload(input, "{}");

    private static string? TryGetInputPropertyAsString(object? input, string key)
    {
        var map = ToJsonMap(input);
        return map.TryGetValue(key, out var value)
            ? GetValueAsString(value, string.Empty)
            : null;
    }

    private static ResponseResult CreateResponseResultFromFinish(AIEventEnvelope envelope, AIFinishEventData finishData)
    {
        var usage = CreateResponseUsageFromFinish(finishData);

        long? completedAt = finishData.CompletedAt switch
        {
            long value => value,
            int value => value,
            JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt64(out var value) => value,
            JsonElement json when json.ValueKind == JsonValueKind.String && long.TryParse(json.GetString(), out var value) => value,
            string text when long.TryParse(text, out var value) => value,
            _ => null
        };

        var status = string.Equals(finishData.FinishReason, "error", StringComparison.OrdinalIgnoreCase)
            ? "failed"
            : "completed";

        return new ResponseResult
        {
            Id = envelope.Id ?? Guid.NewGuid().ToString("N"),
            Object = "response",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CompletedAt = completedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = status,
            Model = finishData.Model ?? "unknown",
            Usage = usage,
            Output = []
        };
    }

    private static JsonElement CreateResponseUsageFromFinish(AIFinishEventData finishData)
    {
        var usage = new Dictionary<string, object?>();

        if (finishData.MessageMetadata is { } messageMetadata && messageMetadata.Usage.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in messageMetadata.Usage.EnumerateObject())
                usage[property.Name] = property.Value.Clone();
        }

        var inputTokens = ExtractUsageInt(usage, "input_tokens") ?? finishData.InputTokens;
        var outputTokens = ExtractUsageInt(usage, "output_tokens") ?? finishData.OutputTokens;
        var totalTokens = ExtractUsageInt(usage, "total_tokens") ?? finishData.TotalTokens;

        if (totalTokens is null && inputTokens is not null && outputTokens is not null)
            totalTokens = inputTokens + outputTokens;

        if (inputTokens is not null)
            usage["input_tokens"] = inputTokens.Value;

        if (outputTokens is not null)
            usage["output_tokens"] = outputTokens.Value;

        if (totalTokens is not null)
            usage["total_tokens"] = totalTokens.Value;

        return JsonSerializer.SerializeToElement(usage, Json);
    }

    private static int? ExtractUsageInt(Dictionary<string, object?> usage, string key)
    {
        if (!usage.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            int number => number,
            long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var number) => number,
            JsonElement json when json.ValueKind == JsonValueKind.String && int.TryParse(json.GetString(), out var number) => number,
            string text when int.TryParse(text, out var number) => number,
            _ => null
        };
    }

    private static bool TryMapSyntheticResponseStreamPart(
        AIStreamEvent streamEvent,
        AIEventEnvelope envelope,
        string kind,
        Dictionary<string, object?> data,
        out ResponseStreamPart part)
    {
        if (kind == "text-start" && envelope.Data is AITextStartEventData)
        {
            var itemState = GetOrCreateReverseItemState(envelope.Id, "message");

            part = new ResponseOutputItemAdded
            {
                SequenceNumber = ResolveReverseSequenceNumber(data),
                OutputIndex = itemState.OutputIndex,
                Item = CreateResponseStreamItem(itemState, status: "in_progress")
            };

            return true;
        }

        if (kind == "text-delta" && envelope.Data is AITextDeltaEventData textDelta)
        {
            var itemState = GetOrCreateReverseItemState(envelope.Id, "message");
            itemState.TextBuffer.Append(textDelta.Delta);

            part = new ResponseOutputTextDelta
            {
                SequenceNumber = ResolveReverseSequenceNumber(data),
                Delta = textDelta.Delta,
                ItemId = itemState.ItemId,
                ContentIndex = itemState.ContentIndex,
                Outputindex = itemState.OutputIndex
            };

            return true;
        }

        if (kind == "text-end" && envelope.Data is AITextEndEventData)
        {
            var itemState = GetOrCreateReverseItemState(envelope.Id, "message");

            part = new ResponseOutputItemDone
            {
                SequenceNumber = ResolveReverseSequenceNumber(data),
                OutputIndex = itemState.OutputIndex,
                Item = CreateResponseStreamItem(
                    itemState,
                    status: "completed",
                    content:
                    [
                        CreateResponseContentPart("output_text", itemState.TextBuffer.ToString())
                    ])
            };

            return true;
        }

        if (kind == "reasoning-start" && envelope.Data is AIReasoningStartEventData reasoningStart)
        {
            var itemState = GetOrCreateReverseItemState(envelope.Id, "reasoning");
            UpdateReverseItemState(itemState, itemType: "reasoning", providerMetadata: reasoningStart.ProviderMetadata);
            itemState.Signature = reasoningStart.Signature ?? ExtractReasoningSignature(reasoningStart.ProviderMetadata, data);

            part = new ResponseOutputItemAdded
            {
                SequenceNumber = ResolveReverseSequenceNumber(data),
                OutputIndex = itemState.OutputIndex,
                Item = CreateResponseStreamItem(itemState, status: "in_progress")
            };

            return true;
        }

        if (kind == "reasoning-delta" && envelope.Data is AIReasoningDeltaEventData reasoningDelta)
        {
            var itemState = GetOrCreateReverseItemState(envelope.Id, "reasoning");
            itemState.ReasoningBuffer.Append(reasoningDelta.Delta);
            itemState.Signature ??= reasoningDelta.Signature ?? ExtractReasoningSignature(reasoningDelta.ProviderMetadata, data);

            part = new ResponseReasoningTextDelta
            {
                SequenceNumber = ResolveReverseSequenceNumber(data),
                OutputIndex = itemState.OutputIndex,
                ItemId = itemState.ItemId,
                ContentIndex = itemState.ContentIndex,
                Delta = reasoningDelta.Delta
            };

            return true;
        }

        if (kind == "reasoning-end" && envelope.Data is AIReasoningEndEventData reasoningEnd)
        {
            var itemState = GetOrCreateReverseItemState(envelope.Id, "reasoning");
            UpdateReverseItemState(itemState, itemType: "reasoning", providerMetadata: reasoningEnd.ProviderMetadata);
            itemState.Signature ??= reasoningEnd.Signature ?? ExtractReasoningSignature(reasoningEnd.ProviderMetadata, data);

            part = new ResponseOutputItemDone
            {
                SequenceNumber = ResolveReverseSequenceNumber(data),
                OutputIndex = itemState.OutputIndex,
                Item = CreateResponseStreamItem(
                    itemState,
                    status: "completed",
                    content:
                    [
                        CreateResponseContentPart("reasoning_text", itemState.ReasoningBuffer.ToString())
                    ])
            };

            return true;
        }

        if (kind == "tool-input-start" && envelope.Data is AIToolInputStartEventData toolInputStart)
        {
            var itemType = ResolveToolItemType(toolInputStart);
            var itemState = GetOrCreateReverseItemState(envelope.Id, itemType);
            UpdateReverseItemState(
                itemState,
                itemType: itemType,
                toolName: toolInputStart.ToolName,
                title: toolInputStart.Title,
                providerExecuted: toolInputStart.ProviderExecuted,
                providerMetadata: toolInputStart.ProviderMetadata);

            part = new ResponseOutputItemAdded
            {
                SequenceNumber = ResolveReverseSequenceNumber(data),
                OutputIndex = itemState.OutputIndex,
                Item = CreateResponseStreamItem(itemState, status: "in_progress")
            };

            return true;
        }

        if (kind == "tool-input-delta" && envelope.Data is AIToolInputDeltaEventData toolInputDelta)
        {
            var itemState = GetOrCreateReverseItemState(envelope.Id, "custom_tool_call");
            itemState.ToolInputBuffer.Append(toolInputDelta.InputTextDelta);

            var sequenceNumber = ResolveReverseSequenceNumber(data);
            part = itemState.ItemType switch
            {
                "function_call" => new ResponseFunctionCallArgumentsDelta
                {
                    SequenceNumber = sequenceNumber,
                    OutputIndex = itemState.OutputIndex,
                    ItemId = itemState.ItemId,
                    Delta = toolInputDelta.InputTextDelta
                },
                "mcp_call" => new ResponseMcpCallArgumentsDelta
                {
                    SequenceNumber = sequenceNumber,
                    OutputIndex = itemState.OutputIndex,
                    ItemId = itemState.ItemId,
                    Delta = toolInputDelta.InputTextDelta
                },
                "code_interpreter_call" => new ResponseCodeInterpreterCallCodeDelta
                {
                    SequenceNumber = sequenceNumber,
                    OutputIndex = itemState.OutputIndex,
                    ItemId = itemState.ItemId,
                    Delta = toolInputDelta.InputTextDelta
                },
                _ => new ResponseCustomToolCallInputDelta
                {
                    SequenceNumber = sequenceNumber,
                    OutputIndex = itemState.OutputIndex,
                    ItemId = itemState.ItemId,
                    Delta = toolInputDelta.InputTextDelta
                }
            };

            return true;
        }

        if (kind == "tool-input-available" && envelope.Data is AIToolInputAvailableEventData toolInputAvailable)
        {
            var itemState = GetOrCreateReverseItemState(
                envelope.Id,
                ResolveToolItemType(new AIToolInputStartEventData
                {
                    ToolName = toolInputAvailable.ToolName,
                    ProviderExecuted = toolInputAvailable.ProviderExecuted,
                    Title = toolInputAvailable.Title
                }));

            UpdateReverseItemState(
                itemState,
                toolName: toolInputAvailable.ToolName,
                title: toolInputAvailable.Title,
                providerExecuted: toolInputAvailable.ProviderExecuted,
                providerMetadata: toolInputAvailable.ProviderMetadata);

            itemState.Input = toolInputAvailable.Input;
            itemState.SerializedInput = SerializeToolInput(toolInputAvailable.Input);

            var sequenceNumber = ResolveReverseSequenceNumber(data);
            part = itemState.ItemType switch
            {
                "function_call" => new ResponseFunctionCallArgumentsDone
                {
                    SequenceNumber = sequenceNumber,
                    OutputIndex = itemState.OutputIndex,
                    ItemId = itemState.ItemId,
                    Arguments = itemState.SerializedInput
                },
                "mcp_call" => new ResponseMcpCallArgumentsDone
                {
                    SequenceNumber = sequenceNumber,
                    OutputIndex = itemState.OutputIndex,
                    ItemId = itemState.ItemId,
                    Arguments = itemState.SerializedInput
                },
                "code_interpreter_call" => new ResponseCodeInterpreterCallDone
                {
                    SequenceNumber = sequenceNumber,
                    OutputIndex = itemState.OutputIndex,
                    ItemId = itemState.ItemId,
                    Code = TryGetInputPropertyAsString(toolInputAvailable.Input, "code") ?? itemState.SerializedInput
                },
                _ => new ResponseCustomToolCallInputDone
                {
                    SequenceNumber = sequenceNumber,
                    OutputIndex = itemState.OutputIndex,
                    ItemId = itemState.ItemId,
                    Input = itemState.SerializedInput
                }
            };

            return true;
        }

        if (kind == "tool-output-available" && envelope.Data is AIToolOutputAvailableEventData toolOutputAvailable)
        {
            var itemState = GetOrCreateReverseItemState(envelope.Id, "custom_tool_call");
            UpdateReverseItemState(
                itemState,
                toolName: toolOutputAvailable.ToolName,
                providerExecuted: toolOutputAvailable.ProviderExecuted,
                providerMetadata: EnsureDownloadFileReverseProviderMetadata(
                    streamEvent.ProviderId,
                    itemState,
                    toolOutputAvailable));
            itemState.Output = toolOutputAvailable.Output;

            part = new ResponseOutputItemDone
            {
                SequenceNumber = ResolveReverseSequenceNumber(data),
                OutputIndex = itemState.OutputIndex,
                Item = CreateResponseStreamItem(itemState, status: "completed")
            };

            return true;
        }

        if (kind == "source-url" && envelope.Data is AISourceUrlEventData sourceUrl)
        {
            var itemState = GetOrCreateReverseItemState(envelope.Id, "message");
            var annotationType = !string.IsNullOrWhiteSpace(sourceUrl.ContainerId)
                ? "container_file_citation"
                : !string.IsNullOrWhiteSpace(sourceUrl.FileId)
                  || !string.IsNullOrWhiteSpace(sourceUrl.Filename)
                  || sourceUrl.Url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? "file_citation"
                    : "url_citation";

            part = new ResponseOutputTextAnnotationAdded
            {
                SequenceNumber = ResolveReverseSequenceNumber(data),
                OutputIndex = itemState.OutputIndex,
                ItemId = itemState.ItemId,
                ContentIndex = itemState.ContentIndex,
                AnnotationIndex = itemState.NextAnnotationIndex++,
                Annotation = CreateResponseAnnotation(annotationType, new Dictionary<string, object?>
                {
                    ["source_id"] = sourceUrl.SourceId,
                    ["url"] = sourceUrl.Url,
                    ["start_index"] = sourceUrl.StartIndex,
                    ["end_index"] = sourceUrl.EndIndex,
                    ["title"] = sourceUrl.Title,
                    ["filename"] = sourceUrl.Filename,
                    ["container_id"] = sourceUrl.ContainerId,
                    ["file_id"] = sourceUrl.FileId,
                    ["provider_metadata"] = sourceUrl.ProviderMetadata
                })
            };

            return true;
        }

        if (kind == "finish" && envelope.Data is AIFinishEventData finishData)
        {
            part = new ResponseCompleted
            {
                SequenceNumber = ResolveReverseSequenceNumber(data),
                Response = CreateResponseResultFromFinish(envelope, finishData)
            };

            ClearReverseStreamState();
            return true;
        }

        if (kind == "error" && envelope.Data is AIErrorEventData errorData)
        {
            part = new ResponseError
            {
                SequenceNumber = ResolveReverseSequenceNumber(data),
                Message = errorData.ErrorText,
                Param = GetValue<string>(data, "param") ?? string.Empty,
                Code = GetValue<string>(data, "code") ?? string.Empty
            };

            ClearReverseStreamState();
            return true;
        }

        part = default!;
        return false;
    }

    private static Dictionary<string, Dictionary<string, object>>? EnsureDownloadFileReverseProviderMetadata(
        string providerId,
        ResponseReverseItemState itemState,
        AIToolOutputAvailableEventData toolOutputAvailable)
    {
        var providerMetadata = toolOutputAvailable.ProviderMetadata ?? itemState.ProviderMetadata;
        var toolName = toolOutputAvailable.ToolName ?? itemState.ToolName ?? itemState.Title;

        if (!string.Equals(toolName, "download_file", StringComparison.OrdinalIgnoreCase))
            return providerMetadata;

        var normalized = providerMetadata is null
            ? new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal)
            : providerMetadata.ToDictionary(
                entry => entry.Key,
                entry => new Dictionary<string, object>(entry.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);

        var targetProviderId = !string.IsNullOrWhiteSpace(providerId)
            ? providerId
            : normalized.Keys.FirstOrDefault(key => !string.IsNullOrWhiteSpace(key)) ?? "provider";

        if (!normalized.TryGetValue(targetProviderId, out var scoped))
        {
            scoped = new Dictionary<string, object>(StringComparer.Ordinal);
            normalized[targetProviderId] = scoped;
        }

        scoped.TryAdd("tool_name", "download_file");
        scoped.TryAdd("name", "download_file");
        scoped.TryAdd("download_tool", true);

        if (TryExtractDownloadFileReversePayload(toolOutputAvailable.Output, out var filename, out var mediaType, out var fileId))
        {
            if (!string.IsNullOrWhiteSpace(filename))
                scoped.TryAdd("filename", filename);

            if (!string.IsNullOrWhiteSpace(mediaType))
                scoped.TryAdd("media_type", mediaType);

            if (!string.IsNullOrWhiteSpace(fileId))
                scoped.TryAdd("file_id", fileId);
        }

        return normalized;
    }

    private static bool TryExtractDownloadFileReversePayload(
        object? output,
        out string? filename,
        out string? mediaType,
        out string? fileId)
    {
        filename = null;
        mediaType = null;
        fileId = null;

        JsonElement payload;
        try
        {
            var json = output switch
            {
                JsonElement jsonElement => jsonElement,
                null => default,
                _ => JsonSerializer.SerializeToElement(output, Json)
            };

            payload = json.ValueKind == JsonValueKind.Object
                && json.TryGetProperty("structuredContent", out var structuredContent)
                && structuredContent.ValueKind == JsonValueKind.Object
                    ? structuredContent
                    : json;
        }
        catch
        {
            return false;
        }

        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        filename = GetJsonString(payload, "filename")
            ?? GetJsonString(payload, "file_name")
            ?? GetJsonString(payload, "file_id");
        mediaType = GetJsonString(payload, "media_type")
            ?? GetJsonString(payload, "mediaType");
        fileId = GetJsonString(payload, "file_id")
            ?? GetJsonString(payload, "fileId");

        return filename is not null || mediaType is not null || fileId is not null;
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private sealed class ResponseReverseStreamState
    {
        public int NextSequenceNumber { get; set; } = 1;

        public int NextOutputIndex { get; set; }

        public Dictionary<string, ResponseReverseItemState> ItemsById { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ResponseReverseItemState
    {
        public required string ItemId { get; init; }

        public required string ItemType { get; set; }

        public int OutputIndex { get; init; }

        public int ContentIndex { get; set; }

        public int NextAnnotationIndex { get; set; }

        public string? ToolName { get; set; }

        public string? Title { get; set; }

        public bool? ProviderExecuted { get; set; }

        public object? Input { get; set; }

        public string? SerializedInput { get; set; }

        public object? Output { get; set; }

        public Dictionary<string, Dictionary<string, object>>? ProviderMetadata { get; set; }

        public string? Signature { get; set; }

        public StringBuilder TextBuffer { get; } = new();

        public StringBuilder ReasoningBuffer { get; } = new();

        public StringBuilder ToolInputBuffer { get; } = new();
    }
}
