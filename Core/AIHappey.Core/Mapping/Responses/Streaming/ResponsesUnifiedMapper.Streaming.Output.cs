using System.Text.Json;
using AIHappey.Responses.Output;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static void UpdateReverseTextMetadata(ResponseReverseItemState state, string providerId, Dictionary<string, object>? metadata)
    {
        if (metadata is null)
            return;
        var scoped = metadata.TryGetValue(providerId, out var provider)
            ? ToJsonMap(provider).ToDictionary(pair => pair.Key, pair => pair.Value!)
            : new Dictionary<string, object>(metadata, StringComparer.Ordinal);
        UpdateReverseItemState(state, providerMetadata: new() { [providerId] = scoped });
        state.NativeItemId ??= GetMetadataString(scoped, "id")
            ?? (GetNativeReverseOutput(state) is { } native ? GetJsonString(native, "id") : null);
    }

    private static JsonElement? GetNativeReverseOutput(ResponseReverseItemState state)
    {
        foreach (var metadata in state.ProviderMetadata?.Values.AsEnumerable() ?? [])
            foreach (var key in new[] { "responses_item", "responses.raw_output" })
                if (metadata.TryGetValue(key, out var value) && value is not null)
                {
                    var native = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, Json);
                    if (native.ValueKind == JsonValueKind.Object && native.TryGetProperty("type", out var type)
                        && type.ValueKind == JsonValueKind.String && type.GetString() == state.ItemType)
                        return native.Clone();
                }
        return null;
    }

    private static ResponseOutputItem CreateCanonicalResponseOutput(ResponseReverseItemState state, string status)
    {
        var id = state.OutputItemId;
        var callId = state.CallId ?? state.ItemId;
        var input = state.SerializedInput ?? (state.ToolInputBuffer.Length > 0 ? state.ToolInputBuffer.ToString() : SerializePayload(state.Input, "{}"));
        var inputMap = ToJsonMap(state.Input);
        var outputMap = ToJsonMap(state.Output);
        var extensions = ReverseOutputValue<Dictionary<string, JsonElement>>(state, "additional_properties") is { } extra
            ? new Dictionary<string, JsonElement>(extra, StringComparer.Ordinal) : [];
        if (state.ProviderMetadata is not null)
            extensions["provider_metadata"] = JsonSerializer.SerializeToElement(state.ProviderMetadata, Json);
        if (state.ProviderExecuted is { } providerExecuted && state.ToolName != GoogleAntigravityStateToolName)
            extensions["provider_executed"] = JsonSerializer.SerializeToElement(providerExecuted);
        if (state.Input is not null && state.ItemType != "custom_tool_call")
            extensions["input"] = JsonSerializer.SerializeToElement(state.Input, Json);
        if (state.Output is not null && state.ItemType is not ("function_call_output" or "mcp_call" or "apply_patch_call_output"))
            extensions["output"] = JsonSerializer.SerializeToElement(state.Output, Json);

        var name = state.ToolName ?? state.Title ?? ReverseOutputValue<string>(state, "name") ?? "tool";
        var itemStatus = CanonicalMessageStatus(status);
        var toolStatus = itemStatus == "incomplete" ? "failed" : itemStatus;
        ResponseOutputItem item = state.ItemType switch
        {
            "message" => new ResponseOutputMessage
            {
                Id = id, Role = ResponseRole.Assistant, Status = itemStatus,
                Phase = ReverseOutputValue<string>(state, "phase"),
                Content = (state.TextBuffer.Length > 0 || state.FileContent.Count == 0
                    ? new List<ResponseOutputContent> { new ResponseOutputText { Text = state.TextBuffer.ToString(), Annotations = [.. state.Annotations] } }
                    : []).Concat(state.FileContent).ToList()
            },
            "reasoning" => new ResponseOutputReasoningItem
            {
                Id = id, Status = itemStatus,
                Summary = ReverseOutputValue<List<ResponseReasoningSummaryTextPart>>(state, "summary") ?? [],
                Content = state.ReasoningBuffer.Length == 0 ? null
                    : [new ResponseOutputReasoningText { Text = state.ReasoningBuffer.ToString() }],
                EncryptedContent = ReverseOutputValue<string>(state, "encrypted_content") ?? state.Signature
            },
            "function_call" => new ResponseOutputItem<ResponseFunctionCallItem>(new()
            {
                Id = id, CallId = callId, Name = name, Arguments = input, Status = itemStatus,
                Caller = state.Caller, Namespace = ReverseOutputValue<string>(state, "namespace")
            }),
            "function_call_output" => new ResponseOutputFunctionCallResult
            {
                Id = id, CallId = callId, Status = itemStatus,
                Output = ResponseOutputConversions.ToToolOutput(state.Output), Caller = state.Caller
            },
            "custom_tool_call" => new ResponseOutputItem<ResponseCustomToolCallItem>(new()
            {
                Id = id, CallId = callId, Name = name, Input = input,
                Caller = state.Caller, Namespace = ReverseOutputValue<string>(state, "namespace")
            }),
            "mcp_call" => new ResponseOutputMcpCall
            {
                Id = id, Name = ReverseOutputValue<string>(state, "name") ?? name,
                Arguments = input, Status = toolStatus,
                ServerLabel = RequiredOutputValue(ReverseOutputValue<string>(state, "server_label")
                    ?? GetValue<string>(inputMap, "server_label"), "mcp_call.server_label"),
                ApprovalRequestId = ReverseOutputValue<string>(state, "approval_request_id"),
                Output = state.Output is null ? null : SerializePayload(state.Output, "{}"),
                Error = ReverseOutputValue<ResponseOutputMcpError>(state, "error")
            },
            "web_search_call" => new ResponseOutputWebSearchCall
            {
                Id = id, Status = toolStatus,
                Action = JsonSerializer.SerializeToElement(CreateWebSearchAction(state.Input), Json)
                    .Deserialize<ResponseOutputWebSearchAction>(ResponseJson.Default)!
            },
            "file_search_call" => new ResponseOutputFileSearchCall
            {
                Id = id, Status = toolStatus,
                Queries = GetValue<List<string>>(inputMap, "queries")
                    ?? (GetValue<string>(inputMap, "query") is { } query ? [query] : []),
                Results = ReverseOutputValue<List<ResponseOutputFileSearchResult>>(state, "results")
                    ?? GetValue<List<ResponseOutputFileSearchResult>>(outputMap, "results")
            },
            "code_interpreter_call" => new ResponseOutputCodeInterpreterCall
            {
                Id = id, Status = toolStatus,
                ContainerId = RequiredOutputValue(ReverseOutputValue<string>(state, "container_id")
                    ?? GetValue<string>(inputMap, "container_id") ?? GetValue<string>(outputMap, "container_id"), "code_interpreter_call.container_id"),
                Code = GetValue<string>(inputMap, "code") ?? (state.Input is string code ? code : null),
                Outputs = ReverseOutputValue<List<ResponseOutputCodeInterpreterResult>>(state, "outputs")
                    ?? GetValue<List<ResponseOutputCodeInterpreterResult>>(outputMap, "outputs")
            },
            "program" => new ResponseOutputItem<ResponseProgramItem>(new()
            {
                Id = id, CallId = callId, Code = GetValue<string>(inputMap, "code") ?? input,
                Fingerprint = state.Fingerprint ?? ReverseOutputValue<string>(state, "fingerprint") ?? string.Empty
            }),
            "apply_patch_call" => new ResponseOutputItem<ResponseApplyPatchCallItem>(new()
            {
                Id = id, CallId = callId, Status = itemStatus == "in_progress" ? "in_progress" : "completed",
                Operation = GetValue<ResponseApplyPatchOperation>(inputMap, "operation")
                    ?? ReverseOutputValue<ResponseApplyPatchOperation>(state, "operation")
                    ?? throw new JsonException("Constructed apply_patch_call requires an operation."),
                Caller = state.Caller, CreatedBy = ReverseOutputValue<string>(state, "created_by")
            }),
            "apply_patch_call_output" => new ResponseOutputItem<ResponseApplyPatchCallOutputItem>(new()
            {
                Id = id, CallId = callId,
                Status = GetValue<string>(outputMap, "status") == "failed" ? "failed" : toolStatus,
                Output = GetValue<string>(outputMap, "output") ?? SerializePayload(state.Output, ""),
                Caller = state.Caller, CreatedBy = ReverseOutputValue<string>(state, "created_by")
            }),
            "image_generation_call" => new ResponseOutputImageGenerationCall
            {
                Id = id, Status = toolStatus, Result = state.ImageBase64, OutputFormat = state.ImageOutputFormat
            },
            _ => throw new JsonException($"Unsupported constructed Responses output kind '{state.ItemType}'.")
        };

        // Status is an extension on custom calls/programs, but still records the
        // runtime lifecycle without altering their canonical input contracts.
        if (state.ItemType is "custom_tool_call" or "program")
            extensions["status"] = JsonSerializer.SerializeToElement(itemStatus);
        item.AdditionalProperties = extensions.Count == 0 ? null : extensions;
        return item;
    }

    private static T? ReverseOutputValue<T>(ResponseReverseItemState state, string key)
    {
        foreach (var metadata in state.ProviderMetadata?.Values.AsEnumerable() ?? [])
        {
            if (GetValue<T>(ToJsonMap(metadata), key) is { } value)
                return value;
        }
        return default;
    }

    private static string RequiredOutputValue(string? value, string field)
        => !string.IsNullOrWhiteSpace(value) ? value
            : throw new JsonException($"Constructed Responses output requires runtime {field}.");

    private static ResponseOutputAnnotation CreateCanonicalSourceAnnotation(AISourceUrlEventData source, int index, int textLength)
    {
        var start = source.StartIndex ?? 0;
        var end = source.EndIndex ?? Math.Max(start, textLength);
        var extensions = new Dictionary<string, JsonElement>
        {
            ["source_id"] = JsonSerializer.SerializeToElement(source.SourceId),
            ["url"] = JsonSerializer.SerializeToElement(source.Url)
        };
        if (source.ProviderMetadata is not null)
            extensions["provider_metadata"] = JsonSerializer.SerializeToElement(source.ProviderMetadata, Json);

        ResponseOutputAnnotation annotation = !string.IsNullOrWhiteSpace(source.ContainerId)
            ? new ResponseOutputContainerFileCitation
            {
                ContainerId = source.ContainerId,
                FileId = source.FileId ?? source.SourceId,
                Filename = source.Filename ?? source.Title ?? source.Url,
                StartIndex = start, EndIndex = end
            }
            : !string.IsNullOrWhiteSpace(source.FileId) || !string.IsNullOrWhiteSpace(source.Filename)
                || source.Url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                ? new ResponseOutputFileCitation
                {
                    FileId = source.FileId ?? source.SourceId,
                    Filename = source.Filename ?? source.Title ?? source.Url, Index = source.StartIndex ?? index
                }
                : new ResponseOutputUrlCitation
                {
                    Url = source.Url, Title = source.Title ?? source.Url,
                    StartIndex = start, EndIndex = end
                };
        // url is a canonical property on URL citations, not extension data.
        if (annotation is ResponseOutputUrlCitation)
            extensions.Remove("url");
        else
        {
            if (source.Title is not null)
                extensions["title"] = JsonSerializer.SerializeToElement(source.Title);
            if (annotation is ResponseOutputFileCitation)
            {
                if (source.StartIndex is not null)
                    extensions["start_index"] = JsonSerializer.SerializeToElement(source.StartIndex.Value);
                if (source.EndIndex is not null)
                    extensions["end_index"] = JsonSerializer.SerializeToElement(source.EndIndex.Value);
            }
        }
        annotation.AdditionalProperties = extensions;
        return annotation;
    }
}
