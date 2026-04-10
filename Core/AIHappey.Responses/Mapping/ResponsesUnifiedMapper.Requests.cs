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
            Model = request.Model,
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
                    Content = ToUnifiedContentParts(message.Content).ToList(),
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
                return new AIInputItem
                {
                    Type = "reasoning",
                    Content = reasoning.Summary
                        .Select(a => (AIContentPart)new AITextContentPart { Type = "text", Text = a.Text, Metadata = new Dictionary<string, object?> { ["type"] = a.Type } })
                        .ToList(),
                    Metadata = new Dictionary<string, object?>
                    {
                        ["id"] = reasoning.Id,
                        ["encrypted_content"] = reasoning.EncryptedContent
                    }
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
            result.AddRange(ToResponsesInputItems(items[i], providerId));

        return result;
    }

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

    private static IEnumerable<ResponseInputItem> ToResponsesInputItems(AIInputItem item, string providerId)
    {
        var kind = item.Type?.Trim().ToLowerInvariant();
        var metadata = item.Metadata ?? new Dictionary<string, object?>();
        var toolParts = (item.Content ?? []).OfType<AIToolCallContentPart>().ToList();
        var nonToolParts = (item.Content ?? []).Where(a => a is not AIToolCallContentPart).ToList();

        if (kind == "message")
        {
            if (nonToolParts.Count > 0 || toolParts.Count == 0)
            {
                yield return new ResponseInputMessage
                {
                    Role = ParseRole(item.Role),
                    Content = new ResponseMessageContent(ToResponsesContentParts(nonToolParts, item.Role).ToList()),
                    Id = ExtractValue<string>(metadata, "id"),
                    Status = ExtractValue<string>(metadata, "status"),
                    Phase = ExtractValue<string>(metadata, "phase")
                };
            }

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
                yield return new ResponseReasoningItem
                {
                    Id = item.Id,
                    EncryptedContent = ExtractNestedValue<string>(metadata, providerId, "encrypted_content")
                };
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
