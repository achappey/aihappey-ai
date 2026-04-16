using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    public static AIRequest ToUnifiedRequest(this ResponseRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        return new AIRequest
        {
            ProviderId = providerId,
            Model = request.Model,
            Instructions = request.Instructions,
            Input = request.Input is null ? null : ToUnifiedInput(request.Input, providerId),
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxOutputTokens,
            Stream = request.Stream,
            ParallelToolCalls = request.ParallelToolCalls,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Select(ToUnifiedTool).ToList(),
            Metadata = BuildUnifiedRequestMetadata(request)
        };
    }

    public static ResponseRequest ToResponseRequest(this AIRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = request.Metadata ?? new Dictionary<string, object?>();

        return new ResponseRequest
        {
            Model = NormalizeRequestModel(request.Model, providerId),
            Instructions = request.Instructions,
            Input = request.Input is null ? null : ToResponsesInput(request.Input, providerId),
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxOutputTokens,
            Stream = request.Stream,
            ParallelToolCalls = request.ParallelToolCalls,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Select(ToResponsesTool).ToList(),
            Metadata = request.Metadata,
            Store = ExtractValue<bool?>(metadata, "responses.store"),
            ServiceTier = ExtractValue<string>(metadata, "responses.service_tier"),
            Text = metadata.TryGetValue("responses.text", out var text) ? text : null,
            TopLogprobs = ExtractValue<int?>(metadata, "responses.top_logprobs"),
            Truncation = ParseTruncation(metadata, "responses.truncation"),
            Reasoning = ExtractObject<Reasoning>(metadata, "responses.reasoning"),
            ContextManagement = ExtractObject<JsonElement[]>(metadata, "responses.context_management")
        };
    }

    private static AIInput ToUnifiedInput(ResponseInput input, string providerId)
    {
        if (input.IsText)
            return new AIInput { Text = input.Text };

        var items = input.Items?.Select(item => ToUnifiedInputItem(item, providerId)).ToList();
        return new AIInput { Items = items };
    }

    private static ResponseInput ToResponsesInput(AIInput input, string providerId)
    {
        if (!string.IsNullOrWhiteSpace(input.Text))
            return new ResponseInput(input.Text);

        var items = BuildResponsesInputItems(input.Items, providerId);
        return new ResponseInput(items);
    }

    private static AIInputItem ToUnifiedInputItem(ResponseInputItem item, string providerId)
    {
        switch (item)
        {
            case ResponseInputMessage message:
                return new AIInputItem
                {
                    Type = "message",
                    Role = message.Role.ToString().ToLowerInvariant(),
                    Content = [.. ToUnifiedContentParts(message.Content)],
                    Metadata = new Dictionary<string, object?>
                    {
                        ["id"] = message.Id,
                        ["status"] = message.Status,
                        ["phase"] = message.Phase
                    }
                };

            case ResponseFunctionCallItem call:
                return new AIInputItem
                {
                    Type = "function_call",
                    Role = "assistant",
                    Content =
                    [
                        new AIToolCallContentPart
                        {
                            Type = "function_call",
                            ToolCallId = call.CallId,
                            ToolName = call.Name,
                            Title = call.Name,
                            Input = ParseJsonString(call.Arguments),
                            State = call.Status,
                            ProviderExecuted = false,
                            Metadata = new Dictionary<string, object?>
                            {
                                ["responses.type"] = call.Type
                            }
                        }
                    ],
                    Metadata = new Dictionary<string, object?>
                    {
                        ["id"] = call.Id,
                        ["call_id"] = call.CallId,
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments,
                        ["status"] = call.Status
                    }
                };

            case ResponseFunctionCallOutputItem output:
                return new AIInputItem
                {
                    Type = "function_call_output",
                    Role = "tool",
                    Content =
                    [
                        new AIToolCallContentPart
                        {
                            Type = "function_call_output",
                            ToolCallId = output.CallId,
                            Output = ParseJsonString(output.Output),
                            State = output.Status,
                            ProviderExecuted = false,
                            Metadata = new Dictionary<string, object?>
                            {
                                ["responses.type"] = output.Type
                            }
                        }
                    ],
                    Metadata = new Dictionary<string, object?>
                    {
                        ["id"] = output.Id,
                        ["call_id"] = output.CallId,
                        ["output"] = output.Output,
                        ["status"] = output.Status
                    }
                };

            case ResponseReasoningItem reasoning:
                var reasoningMetadata = new Dictionary<string, object?>();
                if (!string.IsNullOrWhiteSpace(reasoning.Id))
                    reasoningMetadata["id"] = reasoning.Id;

                MergeProviderScopedReasoningItemIdMetadata(reasoningMetadata, providerId, reasoning.Id);
                MergeProviderScopedEncryptedContentMetadata(reasoningMetadata, providerId, reasoning.EncryptedContent);

                return new AIInputItem
                {
                    Type = "reasoning",
                    Id = reasoning.Id,
                    Content = [.. reasoning.Summary.Select(a => (AIContentPart)new AITextContentPart { Type = "text", Text = a.Text, Metadata = new Dictionary<string, object?> { ["type"] = a.Type } })],
                    Metadata = reasoningMetadata
                };

            case ResponseCompactionItem compaction:
                return new AIInputItem
                {
                    Type = "message",
                    Role = "assistant",
                    Content =
                    [
                        CreateUnifiedCompactionToolPart(
                            providerId,
                            compaction.Id,
                            compaction.EncryptedContent)
                    ],
                    Metadata = CreateCompactionMessageMetadata(
                        providerId,
                        compaction.Id,
                        compaction.EncryptedContent)
                };

            case ResponseImageGenerationCallItem imageGen:
                return new AIInputItem
                {
                    Type = "image_generation_call",
                    Metadata = new Dictionary<string, object?>
                    {
                        ["id"] = imageGen.Id,
                        ["result"] = imageGen.Result,
                        ["status"] = imageGen.Status
                    }
                };

            case ResponseItemReference reference:
                return new AIInputItem
                {
                    Type = "item_reference",
                    Metadata = new Dictionary<string, object?> { ["id"] = reference.Id }
                };

            default:
                return new AIInputItem { Type = item.Type ?? "item", Metadata = new Dictionary<string, object?> { ["raw"] = item } };
        }
    }

    private static List<ResponseInputItem> BuildResponsesInputItems(IReadOnlyList<AIInputItem>? items, string providerId)
    {
        if (items is null || items.Count == 0)
            return [];

        var result = new List<ResponseInputItem>();
        var latestCompaction = FindLatestCompactionToolInvocation(items, providerId);
        var preferEncryptedReasoningReplay = HasProviderScopedEncryptedReasoning(items, providerId);
        var startIndex = 0;

        if (latestCompaction is not null)
        {
            result.Add(new ResponseCompactionItem
            {
                Id = latestCompaction.ItemId,
                EncryptedContent = latestCompaction.EncryptedContent
            });

            startIndex = latestCompaction.ItemIndex + 1;
        }

        for (var i = startIndex; i < items.Count; i++)
            result.AddRange(ToResponsesInputItems(items[i], providerId, preferEncryptedReasoningReplay));

        return MergeConsecutiveUserMessages(result);
    }

    private static List<ResponseInputItem> MergeConsecutiveUserMessages(IReadOnlyList<ResponseInputItem> items)
    {
        var merged = new List<ResponseInputItem>(items.Count);

        foreach (var item in items)
        {
            if (merged.LastOrDefault() is ResponseInputMessage previous
                && item is ResponseInputMessage current
                && CanMergeUserMessages(previous, current))
            {
                merged[^1] = MergeUserMessages(previous, current);
                continue;
            }

            merged.Add(item);
        }

        return merged;
    }

    private static bool CanMergeUserMessages(ResponseInputMessage previous, ResponseInputMessage current)
    {
        if (previous.Role != ResponseRole.User || current.Role != ResponseRole.User)
            return false;

        if (!string.IsNullOrWhiteSpace(previous.Id)
            || !string.IsNullOrWhiteSpace(previous.Status)
            || !string.IsNullOrWhiteSpace(previous.Phase)
            || !string.IsNullOrWhiteSpace(current.Id)
            || !string.IsNullOrWhiteSpace(current.Status)
            || !string.IsNullOrWhiteSpace(current.Phase))
        {
            return false;
        }

        var previousParts = ExpandToInputParts(previous.Content);
        var currentParts = ExpandToInputParts(current.Content);

        return previousParts.Count > 0
               && currentParts.Count > 0
               && (previousParts.Any(static part => part is InputImagePart)
                   || currentParts.Any(static part => part is InputImagePart));
    }

    private static ResponseInputMessage MergeUserMessages(ResponseInputMessage previous, ResponseInputMessage current)
        => new()
        {
            Role = ResponseRole.User,
            Content = new ResponseMessageContent([.. ExpandToInputParts(previous.Content), .. ExpandToInputParts(current.Content)])
        };

    private static IReadOnlyList<ResponseContentPart> ExpandToInputParts(ResponseMessageContent content)
    {
        if (content.IsParts && content.Parts is not null)
            return [.. content.Parts];

        if (content.IsText)
            return [new InputTextPart(content.Text ?? string.Empty)];

        return [];
    }

    private static bool HasProviderScopedEncryptedReasoning(
        IReadOnlyList<AIInputItem> items,
        string providerId)
        => items.Any(item => HasProviderScopedEncryptedReasoning(item, providerId));

    private static bool HasProviderScopedEncryptedReasoning(
        AIInputItem item,
        string providerId)
    {
        if (HasProviderScopedEncryptedContent(item.Metadata, providerId))
            return true;

        foreach (var reasoningPart in item.Content?.OfType<AIReasoningContentPart>() ?? [])
        {
            if (HasProviderScopedEncryptedContent(reasoningPart.Metadata, providerId))
                return true;
        }

        return false;
    }

    private static bool HasProviderScopedEncryptedContent(
        Dictionary<string, object?>? metadata,
        string providerId)
        => metadata is not null
           && !string.IsNullOrWhiteSpace(ExtractNestedValue<string>(metadata, providerId, "encrypted_content"));

    private static CompactionInvocationState? FindLatestCompactionToolInvocation(
        IReadOnlyList<AIInputItem> items,
        string providerId)
    {
        for (var itemIndex = items.Count - 1; itemIndex >= 0; itemIndex--)
        {
            var item = items[itemIndex];
            var toolParts = (item.Content ?? []).OfType<AIToolCallContentPart>().Reverse();

            foreach (var toolPart in toolParts)
            {
                if (!IsCompactionToolCall(toolPart))
                    continue;

                var encryptedContent = toolPart.Metadata is not null
                    ? ExtractNestedValue<string>(toolPart.Metadata, providerId, "encrypted_content")
                    : null;

                encryptedContent ??= item.Metadata is not null
                    ? ExtractNestedValue<string>(item.Metadata, providerId, "encrypted_content")
                    : null;

                if (string.IsNullOrWhiteSpace(encryptedContent))
                    continue;

                return new CompactionInvocationState(
                    itemIndex,
                    ExtractValue<string>(item.Metadata, "id")
                    ?? ExtractValue<string>(toolPart.Metadata, "id")
                    ?? toolPart.ToolCallId,
                    encryptedContent);
            }
        }

        return null;
    }

    private static IEnumerable<ResponseInputItem> ToResponsesInputItems(
        AIInputItem item,
        string providerId,
        bool preferEncryptedReasoningReplay)
    {
        var kind = item.Type?.Trim().ToLowerInvariant();
        var metadata = item.Metadata ?? new Dictionary<string, object?>();
        var toolParts = (item.Content ?? []).OfType<AIToolCallContentPart>().ToList();
        var reasoningParts = (item.Content ?? []).OfType<AIReasoningContentPart>().ToList();
        var nonToolParts = (item.Content ?? []).Where(a => a is not AIToolCallContentPart && a is not AIReasoningContentPart).ToList();

        if (kind == "message")
        {
            foreach (var reasoningPart in SelectReasoningPartsForReplay(reasoningParts, providerId, preferEncryptedReasoningReplay))
            {
                var reasoningItem = CreateResponseReasoningItem(
                    item,
                    metadata,
                    providerId,
                    reasoningPart,
                    requireEncryptedContent: preferEncryptedReasoningReplay);
                if (reasoningItem is not null)
                    yield return reasoningItem;
            }

            if (preferEncryptedReasoningReplay
                && reasoningParts.Count == 0
                && HasProviderScopedEncryptedContent(metadata, providerId))
            {
                var reasoningItem = CreateResponseReasoningItem(
                    item,
                    metadata,
                    providerId,
                    requireEncryptedContent: true);
                if (reasoningItem is not null)
                    yield return reasoningItem;
            }

            if (nonToolParts.Count > 0 || (toolParts.Count == 0 && reasoningParts.Count == 0))
                yield return CreateResponseInputMessage(item, metadata, nonToolParts);

            foreach (var toolPart in toolParts.Where(a => a.IsClientToolCall))
            {
                yield return CreateResponseFunctionCallItem(toolPart, metadata);

                if (HasToolOutput(toolPart))
                    yield return CreateResponseFunctionCallOutputItem(toolPart, metadata);
            }

            yield break;
        }

        switch (kind)
        {
            case "function_call":
            {
                var toolPart = toolParts.FirstOrDefault();
                if (toolPart is not null && toolPart.IsClientToolCall)
                    yield return CreateResponseFunctionCallItem(toolPart, metadata);
                yield break;
            }
            case "function_call_output":
            {
                var toolPart = toolParts.FirstOrDefault();
                if (toolPart is not null && toolPart.IsClientToolCall && HasToolOutput(toolPart))
                    yield return CreateResponseFunctionCallOutputItem(toolPart, metadata);
                yield break;
            }
            case "reasoning":
            {
                var reasoningItem = CreateResponseReasoningItem(
                    item,
                    metadata,
                    providerId,
                    requireEncryptedContent: preferEncryptedReasoningReplay);
                if (reasoningItem is not null)
                    yield return reasoningItem;
                yield break;
            }
            case "compaction":
            {
                var encryptedContent = ExtractNestedValue<string>(metadata, providerId, "encrypted_content");
                if (!string.IsNullOrWhiteSpace(encryptedContent))
                {
                    yield return new ResponseCompactionItem
                    {
                        Id = item.Id ?? ExtractValue<string>(metadata, "id"),
                        EncryptedContent = encryptedContent
                    };
                }

                yield break;
            }
            case "image_generation_call":
            {
                yield return new ResponseImageGenerationCallItem
                {
                    Id = ExtractValue<string>(metadata, "id"),
                    Result = ExtractValue<string>(metadata, "result") ?? string.Empty,
                    Status = ExtractValue<string>(metadata, "status")
                };
                yield break;
            }
            case "item_reference":
            {
                yield return new ResponseItemReference
                {
                    Id = ExtractValue<string>(metadata, "id") ?? string.Empty
                };
                yield break;
            }
            default:
                yield return new ResponseInputMessage
                {
                    Role = ParseRole(item.Role),
                    Content = new ResponseMessageContent(ToResponsesContentParts(nonToolParts, item.Role).ToList())
                };
                yield break;
        }
    }

    private static ResponseInputMessage CreateResponseInputMessage(
        AIInputItem item,
        Dictionary<string, object?> metadata,
        IReadOnlyCollection<AIContentPart> parts)
        => new()
        {
            Role = ParseRole(item.Role),
            Content = new ResponseMessageContent(ToResponsesContentParts(parts, item.Role).ToList()),
            Id = ExtractValue<string>(metadata, "id"),
            Status = ExtractValue<string>(metadata, "status"),
            Phase = ExtractValue<string>(metadata, "phase")
        };

    private static ResponseReasoningItem? CreateResponseReasoningItem(
        AIInputItem item,
        Dictionary<string, object?> metadata,
        string providerId,
        AIReasoningContentPart? reasoningPart = null,
        bool requireEncryptedContent = false)
    {
        var reasoningMetadata = reasoningPart?.Metadata;

        var encryptedContent = reasoningMetadata is not null
            ? ExtractNestedValue<string>(reasoningMetadata, providerId, "encrypted_content")
            : null;

        encryptedContent ??= ExtractNestedValue<string>(metadata, providerId, "encrypted_content");

        var summary = reasoningMetadata is not null
            ? ExtractNestedValue<List<ResponseReasoningSummaryTextPart>>(reasoningMetadata, providerId, "summary")
            : null;

        summary ??= ExtractNestedValue<List<ResponseReasoningSummaryTextPart>>(metadata, providerId, "summary");

        if (summary is null || summary.Count == 0)
        {
            summary = [];

            if (!string.IsNullOrWhiteSpace(reasoningPart?.Text))
            {
                summary.Add(new ResponseReasoningSummaryTextPart
                {
                    Text = reasoningPart.Text
                });
            }
            else if (string.IsNullOrWhiteSpace(encryptedContent))
            {
                foreach (var textPart in item.Content?.OfType<AITextContentPart>() ?? [])
                {
                    if (string.IsNullOrWhiteSpace(textPart.Text))
                        continue;

                    summary.Add(new ResponseReasoningSummaryTextPart
                    {
                        Type = ExtractValue<string>(textPart.Metadata, "type") ?? "summary_text",
                        Text = textPart.Text
                    });
                }
            }
        }

        if (requireEncryptedContent && string.IsNullOrWhiteSpace(encryptedContent))
            return null;

        if (summary.Count == 0 && string.IsNullOrWhiteSpace(encryptedContent))
            return null;

        var reasoningItemId = ResolveReasoningItemId(item, metadata, reasoningMetadata, providerId);

        return new ResponseReasoningItem
        {
            Id = reasoningItemId,
            Summary = summary,
            EncryptedContent = encryptedContent
        };
    }

    private static string? ResolveReasoningItemId(
        AIInputItem item,
        Dictionary<string, object?> metadata,
        Dictionary<string, object?>? reasoningMetadata,
        string providerId)
    {
        var itemType = item.Type?.Trim().ToLowerInvariant();
        var providerScopedReasoningId = reasoningMetadata is not null
            ? ExtractNestedValue<string>(reasoningMetadata, providerId, "id")
              ?? ExtractNestedValue<string>(reasoningMetadata, providerId, "item_id")
            : null;

        var providerScopedItemId = ExtractNestedValue<string>(metadata, providerId, "id")
                                   ?? ExtractNestedValue<string>(metadata, providerId, "item_id");

        return providerScopedReasoningId
               ?? ExtractValue<string>(reasoningMetadata, "id")
               ?? ExtractValue<string>(reasoningMetadata, "item_id")
               ?? (string.Equals(itemType, "reasoning", StringComparison.OrdinalIgnoreCase) ? item.Id : null)
               ?? (string.Equals(itemType, "reasoning", StringComparison.OrdinalIgnoreCase) ? providerScopedItemId : null)
               ?? (string.Equals(itemType, "reasoning", StringComparison.OrdinalIgnoreCase) ? ExtractValue<string>(metadata, "id") : null)
               ?? (string.Equals(itemType, "reasoning", StringComparison.OrdinalIgnoreCase) ? ExtractValue<string>(metadata, "item_id") : null);
    }

    private static IEnumerable<AIReasoningContentPart> SelectReasoningPartsForReplay(
        IReadOnlyCollection<AIReasoningContentPart> reasoningParts,
        string providerId,
        bool preferEncryptedReasoningReplay)
    {
        if (!preferEncryptedReasoningReplay)
        {
            foreach (var reasoningPart in reasoningParts)
                yield return reasoningPart;

            yield break;
        }

        var seenEncryptedContents = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reasoningPart in reasoningParts)
        {
            var encryptedContent = ExtractNestedValue<string>(reasoningPart.Metadata ?? new Dictionary<string, object?>(), providerId, "encrypted_content");
            if (string.IsNullOrWhiteSpace(encryptedContent))
                continue;

            if (!seenEncryptedContents.Add(encryptedContent))
                continue;

            yield return reasoningPart;
        }
    }

    private static T? ExtractNestedValue<T>(
        Dictionary<string, object?> metadata,
        string providerId,
        string key)
    {
        if (metadata.TryGetValue(providerId, out var providerObj)
            && TryGetJsonObject(providerObj, out var providerJson)
            && providerJson.TryGetProperty(key, out var value))
        {
            return value.Deserialize<T>();
        }

        return default;
    }

    private static bool TryGetJsonObject(object? value, out JsonElement json)
    {
        switch (value)
        {
            case JsonElement element when element.ValueKind == JsonValueKind.Object:
                json = element;
                return true;
            case Dictionary<string, object> dict:
                json = JsonSerializer.SerializeToElement(dict, JsonSerializerOptions.Web);
                return true;
          //  case Dictionary<string, object?> nullableDict:
           //     json = JsonSerializer.SerializeToElement(nullableDict, JsonSerializerOptions.Web);
            //    return true;
            case null:
                json = default;
                return false;
            default:
                try
                {
                    json = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
                    return json.ValueKind == JsonValueKind.Object;
                }
                catch
                {
                    json = default;
                    return false;
                }
        }
    }

    private static Dictionary<string, object?> BuildUnifiedRequestMetadata(ResponseRequest request)
        => new()
        {
            ["responses.metadata"] = request.Metadata,
            ["responses.store"] = request.Store,
            ["responses.service_tier"] = request.ServiceTier,
            ["responses.include"] = request.Include,
            ["responses.text"] = request.Text,
            ["responses.top_logprobs"] = request.TopLogprobs,
            ["responses.truncation"] = request.Truncation,
            ["responses.reasoning"] = request.Reasoning,
            ["responses.context_management"] = request.ContextManagement
        };

    private static ResponseRole ParseRole(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            "assistant" => ResponseRole.Assistant,
            "system" => ResponseRole.System,
            "developer" => ResponseRole.Developer,
            _ => ResponseRole.User
        };

    private sealed record CompactionInvocationState(int ItemIndex, string? ItemId, string EncryptedContent);
}
