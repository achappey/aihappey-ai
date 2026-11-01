using System.Text.Json;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static bool IsApplyPatchPart(AIToolCallContentPart part, string? callType = null, string? resultType = null)
        => part.Type is "apply_patch_call" or "apply_patch_call_output"
            || callType is "apply_patch_call" or "apply_patch_call_output"
            || resultType == "apply_patch_call_output";

    private static AIToolCallContentPart CreateUnifiedApplyPatchPart(
        object raw, Dictionary<string, object?> map, string providerId, int outputIndex)
    {
        var isOutput = GetValue<string>(map, "type") == "apply_patch_call_output";
        return new AIToolCallContentPart
        {
            Type = isOutput ? "apply_patch_call_output" : "apply_patch_call",
            ToolCallId = GetValue<string>(map, "call_id") ?? throw new JsonException("Apply Patch call_id is required."),
            ToolName = "apply_patch",
            Title = "Apply Patch",
            Input = isOutput ? null : new { operation = GetValue<object>(map, "operation") },
            Output = isOutput ? new { status = GetValue<string>(map, "status"), output = GetValue<string>(map, "output") } : null,
            State = GetValue<string>(map, "status"),
            ProviderExecuted = false,
            Metadata = CreateResponsesToolMetadata(providerId, isOutput ? "apply_patch_call_output" : "apply_patch_call", raw, map, outputIndex)
        };
    }

    private static AIInputItem CreateUnifiedApplyPatchInputItem(ResponseInputItem item, string providerId)
    {
        var map = ToJsonMap(item);
        var part = CreateUnifiedApplyPatchPart(item, map, providerId, 0);
        return new AIInputItem
        {
            Type = item.Type!, Role = item is ResponseApplyPatchCallOutputItem ? "tool" : "assistant",
            Content = [part], Metadata = part.Metadata
        };
    }

    private static IEnumerable<ResponseInputItem> CreateApplyPatchReplayItems(
        AIToolCallContentPart part, Dictionary<string, object?> itemMetadata, string providerId)
    {
        var metadata = part.Metadata ?? [];
        T? Value<T>(string key, string channel = "messages.provider.call.metadata")
            => ExtractNestedChannelValue<T>(metadata, channel, providerId, key)
                ?? ExtractNestedValue<T>(metadata, providerId, key)
                ?? ExtractNestedValue<T>(itemMetadata, providerId, key);

        var outputOnly = part.Type == "apply_patch_call_output"
            || ResolveResponsesReplayType(metadata, providerId, "messages.provider.call.metadata") == "apply_patch_call_output";
        var callId = Value<string>("call_id") ?? part.ToolCallId;
        if (!outputOnly)
        {
            var input = JsonSerializer.SerializeToElement(part.Input, Json);
            if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("operation", out var operation))
                throw new JsonException("Apply Patch operation is required for native replay.");
            yield return new ResponseApplyPatchCallItem
            {
                Id = Value<string>("id"), CallId = callId,
                Status = Value<string>("status") == "in_progress" ? "in_progress" : "completed",
                Operation = operation.Deserialize<ResponseApplyPatchOperation>(Json)!,
                Caller = Value<ResponseCaller>("caller"), CreatedBy = Value<string>("created_by")
            };
        }

        if (HasToolOutput(part) || part.Approval?.Approved == false || part.State is "output-error" or "output-denied")
        {
            var output = JsonSerializer.SerializeToElement(part.Output, Json);
            var result = output;
            if (output.ValueKind == JsonValueKind.Object && output.TryGetProperty("structuredContent", out var structured)) result = structured;
            var failed = part.Approval?.Approved == false || part.State is "output-error" or "output-denied" or "failed"
                || output.ValueKind == JsonValueKind.Object && output.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
            var status = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
            var text = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("output", out var textValue) && textValue.ValueKind == JsonValueKind.String
                ? textValue.GetString() : output.ValueKind == JsonValueKind.String ? output.GetString() : output.GetRawText();
            if (part.Approval?.Approved == false)
                text = part.Approval.Reason ?? "The user denied release of this tool result. File changes were not rolled back.";
            yield return new ResponseApplyPatchCallOutputItem
            {
                Id = outputOnly ? Value<string>("id") : ExtractNestedChannelValue<string>(metadata, "messages.provider.result.metadata", providerId, "id"),
                CallId = callId, Status = failed || status == "failed" ? "failed" : "completed", Output = text,
                Caller = Value<ResponseCaller>("caller"),
                CreatedBy = outputOnly ? Value<string>("created_by") : ExtractNestedChannelValue<string>(metadata, "messages.provider.result.metadata", providerId, "created_by")
            };
        }
    }

    private static Dictionary<string, Dictionary<string, object>>? CreateApplyPatchStreamMetadata(string providerId, ResponseStreamItem item)
        => CreateProviderMetadata(providerId, new Dictionary<string, object?>
        {
            ["type"] = item.Type, ["id"] = item.Id, ["call_id"] = item.CallId, ["status"] = item.Status,
            ["operation"] = item.Operation,
            ["caller"] = GetAdditionalPropertyValue(item.AdditionalProperties, "caller"),
            ["created_by"] = GetAdditionalPropertyValue(item.AdditionalProperties, "created_by")
        });
}
