using System.Text.Json;
using AIHappey.Responses.Output;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    // The object boundary exists only for the public result contract and opaque
    // native replay. All locally constructed items below belong to the output union.
    private static IEnumerable<object> ToResponseOutputObjects(AIOutput? output, string providerId, string? status)
    {
        foreach (var item in output?.Items ?? [])
        {
            if (ExtractObject<object>(item.Metadata, "responses.raw_output") is { } native)
            {
                yield return native;
                continue;
            }
            var replay = ExtractNestedValue<JsonElement>(item.Metadata ?? [], providerId, "responses_item");
            if (replay.ValueKind == JsonValueKind.Object)
            {
                yield return replay.Clone();
                var resultReplay = ExtractNestedValue<JsonElement>(item.Metadata ?? [], providerId, "result_responses_item");
                if (resultReplay.ValueKind == JsonValueKind.Object)
                    yield return resultReplay.Clone();
                continue;
            }

            var nativeTools = (item.Content ?? []).OfType<AIToolCallContentPart>()
                .Where(tool => ExtractObject<object>(tool.Metadata, "responses.raw_output") is not null).ToList();
            var constructedItem = nativeTools.Count == 0 ? item : new AIOutputItem
            {
                Type = item.Type, Role = item.Role, Metadata = item.Metadata,
                Content = (item.Content ?? []).Where(part => part is not AIToolCallContentPart tool || !nativeTools.Contains(tool)).ToList()
            };
            if (nativeTools.Count == 0 || constructedItem.Content?.Count > 0)
                foreach (var constructed in ToCanonicalOutputItems(constructedItem, providerId, status))
                    yield return constructed;
            foreach (var tool in nativeTools)
                yield return ExtractObject<object>(tool.Metadata, "responses.raw_output")!;
        }
    }

    private static IEnumerable<ResponseOutputItem> ToCanonicalOutputItems(AIOutputItem item, string providerId, string? responseStatus)
    {
        var metadata = item.Metadata ?? [];
        var itemId = ResolveConstructedItemId(metadata, providerId, item.Type == "reasoning" ? "rs" : "msg");
        var itemStatus = CanonicalMessageStatus(ExtractNestedValue<string>(metadata, providerId, "status")
            ?? ExtractValue<string>(metadata, "status") ?? responseStatus);
        var content = new List<ResponseOutputContent>();
        var reasoningParts = new List<AIReasoningContentPart>();
        foreach (var part in item.Content ?? [])
        {
            switch (part)
            {
                case AITextContentPart text:
                    content.Add(new ResponseOutputText
                    {
                        Text = text.Text,
                        Annotations = ReadOutputMetadata<List<ResponseOutputAnnotation>>(text.Metadata, "responses.annotations") ?? [],
                        Logprobs = ReadOutputMetadata<List<ResponseOutputLogprob>>(text.Metadata, "responses.logprobs"),
                        AdditionalProperties = OutputExtensions(text.Metadata, providerId)
                    });
                    break;
                case AIReasoningContentPart reasoning:
                    reasoningParts.Add(reasoning);
                    break;
                case AIFileContentPart file:
                    content.Add(ToCanonicalFileContent(file));
                    break;
            }
        }

        if (reasoningParts.Count > 0 || item.Type == "reasoning")
        {
            var summary = ExtractNestedValue<List<ResponseReasoningSummaryTextPart>>(metadata, providerId, "summary") ?? [];
            yield return new ResponseOutputReasoningItem
            {
                Id = item.Type == "reasoning" ? itemId : $"{itemId}:reasoning",
                Status = itemStatus,
                Summary = summary,
                Content = reasoningParts.Where(part => part.Text is { Length: > 0 })
                    .Select(part => new ResponseOutputReasoningText { Text = part.Text! }).ToList(),
                EncryptedContent = ExtractNestedValue<string>(metadata, providerId, "encrypted_content")
                    ?? reasoningParts.Select(part => ExtractNestedValue<string>(part.Metadata ?? [], providerId, "encrypted_content")
                        ?? part.Signature).FirstOrDefault(value => value is not null),
                AdditionalProperties = OutputExtensions(metadata, providerId)
            };
        }

        var tools = (item.Content ?? []).OfType<AIToolCallContentPart>().ToList();
        if (content.Count > 0 || tools.Count == 0 && reasoningParts.Count == 0 && item.Type != "reasoning")
        {
            yield return new ResponseOutputMessage
            {
                Id = item.Type == "reasoning" ? $"{itemId}:message" : itemId,
                Role = ResponseRole.Assistant,
                Status = itemStatus,
                Phase = ExtractNestedValue<string>(metadata, providerId, "phase") ?? ExtractValue<string>(metadata, "phase"),
                Content = content,
                AdditionalProperties = OutputExtensions(metadata, providerId)
            };
        }

        foreach (var tool in tools)
        {
            if (IsSyntheticImageToolCall(tool))
            {
                var images = ExtractImageToolOutputs(tool.Output).ToList();
                var imageId = ResolveConstructedItemId(tool.Metadata, providerId, "ig", tool.ToolCallId);
                var imageExtensions = OutputExtensions(tool.Metadata, providerId) ?? [];
                if (tool.Input is not null)
                    imageExtensions["input"] = JsonSerializer.SerializeToElement(tool.Input, Json);
                if (tool.Output is not null)
                    imageExtensions["output"] = JsonSerializer.SerializeToElement(tool.Output, Json);
                if (images.Count == 0)
                {
                    var imageStatus = CanonicalMessageStatus(tool.State ?? responseStatus);
                    yield return new ResponseOutputImageGenerationCall
                    {
                        Id = imageId, Status = imageStatus == "incomplete" ? "failed" : imageStatus,
                        Result = null, AdditionalProperties = imageExtensions.Count == 0 ? null : imageExtensions
                    };
                    continue;
                }
                var imageIndex = 0;
                foreach (var image in images)
                    yield return new ResponseOutputImageGenerationCall
                    {
                        Id = imageIndex++ == 0 ? imageId : $"{imageId}_{imageIndex}",
                        Status = "completed", Result = image.Base64,
                        OutputFormat = image.MediaType.Split('/').LastOrDefault(),
                        AdditionalProperties = imageExtensions.Count == 0 ? null : imageExtensions
                    };
                continue;
            }

            if (IsCompactionToolCall(tool))
            {
                var encrypted = ExtractNestedValue<string>(tool.Metadata ?? [], providerId, "encrypted_content")
                    ?? ExtractNestedValue<string>(metadata, providerId, "encrypted_content");
                if (encrypted is null)
                    throw new JsonException("Constructed compaction output requires encrypted_content.");
                yield return new ResponseOutputItem<ResponseCompactionItem>(new()
                {
                    Id = ResolveConstructedItemId(tool.Metadata ?? metadata, providerId, "cmp"),
                    EncryptedContent = encrypted,
                    AdditionalProperties = OutputExtensions(tool.Metadata, providerId)
                });
                continue;
            }

            if (IsApplyPatchPart(tool,
                ResolveResponsesReplayType(tool.Metadata, providerId, "messages.provider.call.metadata"),
                ResolveResponsesReplayType(tool.Metadata, providerId, "messages.provider.result.metadata")))
            {
                var patchCallId = NonemptyId(tool.ToolCallId, "call");
                foreach (var patch in CreateApplyPatchReplayItems(tool, metadata, providerId))
                {
                    if (patch is ResponseApplyPatchCallItem call)
                    {
                        call.Id = NonemptyId(call.Id, "apc");
                        call.CallId = NonemptyId(call.CallId, "call", patchCallId);
                        yield return new ResponseOutputItem<ResponseApplyPatchCallItem>(call);
                    }
                    else if (patch is ResponseApplyPatchCallOutputItem result)
                    {
                        result.Id = NonemptyId(result.Id, "apco");
                        result.CallId = NonemptyId(result.CallId, "call", patchCallId);
                        yield return new ResponseOutputItem<ResponseApplyPatchCallOutputItem>(result);
                    }
                }
                continue;
            }

            var toolMetadata = tool.Metadata ?? [];
            var callId = NonemptyId(tool.ToolCallId, "call");
            var callItemId = ResolveConstructedItemId(toolMetadata, providerId, "fc", callId);
            var status = CanonicalMessageStatus(tool.State ?? (tool.Output is not null ? "completed" : responseStatus));
            var extensions = OutputExtensions(tool.Metadata, providerId) ?? [];
            if (tool.ProviderExecuted is not null && !IsGoogleAntigravityStateToolPart(tool))
                extensions["provider_executed"] = JsonSerializer.SerializeToElement(tool.ProviderExecuted.Value);

            if (tool.IsClientToolCall || tool.IsSyntheticProviderExecutedSpeech() || IsGoogleAntigravityStateToolPart(tool))
            {
                yield return new ResponseOutputItem<ResponseFunctionCallItem>(new()
                {
                    Id = callItemId, CallId = callId, Name = tool.ToolName ?? tool.Title ?? "tool",
                    Arguments = SerializePayload(tool.Input, "{}"), Status = status,
                    Namespace = ExtractResponsesNamespace(tool.Metadata, metadata, providerId),
                    Caller = ExtractResponsesCaller(tool.Metadata, metadata, providerId), AdditionalProperties = extensions
                });
                if (HasToolOutput(tool))
                    yield return new ResponseOutputFunctionCallResult
                    {
                        Id = ResolveConstructedResultId(toolMetadata, providerId, callItemId), CallId = callId,
                        Output = ResponseOutputConversions.ToToolOutput(CreateToolOutputValue(tool)),
                        Status = CanonicalMessageStatus(tool.State ?? "completed"),
                        Caller = ExtractResponsesCaller(tool.Metadata, metadata, providerId)
                    };
                continue;
            }

            // Provider tool kinds share the same canonical construction used by
            // streaming completion. Their arbitrary input/output remains available
            // as extension data in addition to kind-specific contract fields.
            var state = new ResponseReverseItemState
            {
                ItemId = callItemId, CallId = callId,
                ItemType = ResolveToolItemType(new AIToolInputStartEventData
                {
                    ToolName = tool.ToolName, ProviderExecuted = tool.ProviderExecuted
                }),
                ToolName = tool.ToolName, Title = tool.Title, ProviderExecuted = tool.ProviderExecuted,
                Input = tool.Input, Output = tool.Output, SerializedInput = SerializePayload(tool.Input, "{}"),
                ProviderMetadata = new() { [providerId] = (ExtractObject<Dictionary<string, object?>>(toolMetadata, providerId)
                    ?? toolMetadata).ToDictionary(pair => pair.Key, pair => pair.Value!) },
                Status = status
            };
            yield return CreateCanonicalResponseOutput(state, status);
        }
    }

    private static ResponseOutputContent ToCanonicalFileContent(AIFileContentPart file)
    {
        var data = file.Data switch
        {
            byte[] bytes => $"data:{file.MediaType ?? "application/octet-stream"};base64,{Convert.ToBase64String(bytes)}",
            ReadOnlyMemory<byte> bytes => $"data:{file.MediaType ?? "application/octet-stream"};base64,{Convert.ToBase64String(bytes.Span)}",
            Memory<byte> bytes => $"data:{file.MediaType ?? "application/octet-stream"};base64,{Convert.ToBase64String(bytes.Span)}",
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            JsonElement json => json.GetRawText(),
            string text => text,
            Uri uri => uri.OriginalString,
            null => null,
            _ => SerializePayload(file.Data, string.Empty)
        };
        var fileId = ExtractValue<string>(file.Metadata, "responses.file_id");
        var extensions = new Dictionary<string, JsonElement>();
        if (file.MediaType is not null)
            extensions["media_type"] = JsonSerializer.SerializeToElement(file.MediaType);
        if (file.Metadata is not null)
            extensions["provider_metadata"] = JsonSerializer.SerializeToElement(file.Metadata, Json);
        return new InputFilePart
        {
            Filename = file.Filename, FileId = fileId,
            FileUrl = data?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? data : ExtractValue<string>(file.Metadata, "responses.file_url"),
            FileData = data is not null && data != fileId && !data.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? data : null,
            AdditionalProperties = extensions.Count == 0 ? null : extensions
        }.ToOutputContent();
    }

    private static string ResolveConstructedItemId(Dictionary<string, object?>? metadata, string providerId, string prefix, string? fallback = null)
        => NonemptyId(ExtractNestedValue<string>(metadata ?? [], providerId, "id")
            ?? ExtractValue<string>(metadata, "id"), prefix, fallback);

    private static string ResolveConstructedResultId(Dictionary<string, object?> metadata, string providerId, string callItemId)
    {
        var id = ExtractNestedChannelValue<string>(metadata, "messages.provider.result.metadata", providerId, "id")
            ?? ExtractValue<string>(metadata, "output_item_id");
        return string.IsNullOrWhiteSpace(id) || id == callItemId ? $"{callItemId}-output" : id;
    }

    private static string NonemptyId(string? id, string prefix, string? fallback = null)
        => !string.IsNullOrWhiteSpace(id) ? id : !string.IsNullOrWhiteSpace(fallback) ? fallback : $"{prefix}_{Guid.NewGuid():N}";

    private static string CanonicalMessageStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "in_progress" or "queued" or "input-streaming" or "input-available" or "approval-requested" or "running" or "pending" => "in_progress",
        "incomplete" or "failed" or "cancelled" or "canceled" or "output-error" or "output-denied" or "error" => "incomplete",
        _ => "completed"
    };

    private static Dictionary<string, JsonElement>? OutputExtensions(Dictionary<string, object?>? metadata, string providerId)
    {
        var extensions = ReadOutputMetadata<Dictionary<string, JsonElement>>(metadata, "responses.additional_properties")
            ?? ExtractNestedValue<Dictionary<string, JsonElement>>(metadata ?? [], providerId, "additional_properties");
        var copy = extensions?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal) ?? [];
        if (metadata is { Count: > 0 })
            copy["provider_metadata"] = JsonSerializer.SerializeToElement(metadata, Json);
        return copy.Count == 0 ? null : copy;
    }

    private static T? ReadOutputMetadata<T>(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return default;
        return value is T typed ? typed
            : JsonSerializer.SerializeToElement(value, ResponseJson.Default).Deserialize<T>(ResponseJson.Default);
    }
}
