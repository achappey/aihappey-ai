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
            Headers = request.Headers,
            Metadata = request.Metadata
        };
    }

    public static ResponseRequest ToResponseRequest(this AIRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = request.Metadata ?? [];

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
            Headers = request.Headers,
            PromptCacheKey = request.Id,
            Metadata = request.Metadata,
            Store = ExtractValue<bool?>(metadata, "responses.store"),
            ServiceTier = ExtractValue<string>(metadata, "responses.service_tier"),
            Text = metadata.TryGetValue("responses.text", out var text) ? text : null,
            TopLogprobs = ExtractValue<int?>(metadata, "responses.top_logprobs"),
            PromptCacheOptions = ExtractObject<object>(metadata, "responses.prompt_cache_options"),
            // Truncation = ParseTruncation(metadata, "responses.truncation"),
            Reasoning = ExtractObject<Reasoning>(metadata, "responses.reasoning"),
            ContextManagement = ExtractObject<JsonElement[]>(metadata, "responses.context_management")
        };
    }

    private static AIInput ToUnifiedInput(ResponseInput input, string providerId)
    {
        if (input.IsText)
            return new AIInput
            {
                Items =
                [
                    new AIInputItem()
                    {
                        Type = "message",
                        Role = "user",
                        Content = [new AITextContentPart() {
                            Text = input.Text!,
                            Type = "text"
                        }]
                    }
                ]
            };

        List<AIInputItem>? items = input.Items?.Select(item => ToUnifiedInputItem(item, providerId))
            .ToList();

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
                var callPartMetadata = CreateResponsesReplayMetadata(providerId, call.Type, call.Caller);
                if (!string.IsNullOrWhiteSpace(call.Namespace))
                    GetOrCreateProviderScopedMetadata(callPartMetadata, providerId)["namespace"] = call.Namespace;
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
                            Metadata = callPartMetadata
                        }
                    ],
                    Metadata = new Dictionary<string, object?>
                    {
                        ["id"] = call.Id,
                        ["call_id"] = call.CallId,
                        ["name"] = call.Name,
                        ["namespace"] = call.Namespace,
                        ["arguments"] = call.Arguments,
                        ["status"] = call.Status,
                        [providerId] = GetOrCreateProviderScopedMetadata(callPartMetadata, providerId)
                    }
                };

            case ResponseFunctionCallOutputItem output:
                var outputPartMetadata = CreateResponsesReplayMetadata(providerId, output.Type, output.Caller);
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
                            Metadata = outputPartMetadata
                        }
                    ],
                    Metadata = new Dictionary<string, object?>
                    {
                        ["id"] = output.Id,
                        ["call_id"] = output.CallId,
                        ["output"] = output.Output,
                        ["status"] = output.Status,
                        [providerId] = GetOrCreateProviderScopedMetadata(outputPartMetadata, providerId)
                    }
                };

            case ResponseCodeInterpreterCallItem codeInterpreter:
                var codeInterpreterMetadata = CreateResponsesReplayMetadata(providerId, codeInterpreter.Type, codeInterpreter.Caller);
                var codeInterpreterProviderMetadata = GetOrCreateProviderScopedMetadata(codeInterpreterMetadata, providerId);
                codeInterpreterProviderMetadata["id"] = codeInterpreter.Id;
                codeInterpreterProviderMetadata["item_id"] = codeInterpreter.Id;
                codeInterpreterProviderMetadata["code"] = codeInterpreter.Code;
                codeInterpreterProviderMetadata["container_id"] = codeInterpreter.ContainerId;
                codeInterpreterProviderMetadata["outputs"] = codeInterpreter.Outputs.Clone();
                codeInterpreterProviderMetadata["status"] = codeInterpreter.Status;

                return new AIInputItem
                {
                    Type = "code_interpreter_call",
                    Role = "assistant",
                    Content =
                    [
                        new AIToolCallContentPart
                        {
                            Type = "tool-code_interpreter",
                            ToolCallId = codeInterpreter.Id,
                            ToolName = "code_interpreter",
                            Title = "code_interpreter",
                            Input = new { code = codeInterpreter.Code },
                            Output = codeInterpreter.Outputs.Clone(),
                            State = codeInterpreter.Status,
                            ProviderExecuted = true,
                            Metadata = codeInterpreterMetadata
                        }
                    ],
                    Metadata = codeInterpreterMetadata
                };

            case ResponseWebSearchCallItem webSearch:
                var webSearchMetadata = CreateResponsesReplayMetadata(providerId, webSearch.Type);
                var webSearchScopedMetadata = GetOrCreateProviderScopedMetadata(webSearchMetadata, providerId);
                webSearchScopedMetadata["id"] = webSearch.Id;
                webSearchScopedMetadata["status"] = webSearch.Status;
                webSearchScopedMetadata["action"] = webSearch.Action.Clone();
                return new AIInputItem
                {
                    Type = "web_search_call",
                    Role = "assistant",
                    Content =
                    [
                        new AIToolCallContentPart
                        {
                            Type = "tool-web_search_call",
                            ToolCallId = webSearch.Id,
                            ToolName = "web_search",
                            Title = "web_search",
                            Input = webSearch.Action.Clone(),
                            State = webSearch.Status,
                            ProviderExecuted = true,
                            Metadata = webSearchMetadata
                        }
                    ],
                    Metadata = webSearchMetadata
                };

            case ResponseProgramItem program:
                return CreateUnifiedProgramInputItem(program, providerId);

            case ResponseProgramOutputItem programOutput:
                return CreateUnifiedProgramOutputInputItem(programOutput, providerId);

            case ResponseToolSearchCallItem toolSearchCall:
                return CreateUnifiedToolSearchCallInputItem(toolSearchCall, providerId);

            case ResponseToolSearchOutputItem toolSearchOutput:
                return CreateUnifiedToolSearchOutputInputItem(toolSearchOutput, providerId);

            case ResponseReasoningItem reasoning:
                var reasoningMetadata = new Dictionary<string, object?>();
                if (!string.IsNullOrWhiteSpace(reasoning.Id))
                    reasoningMetadata["id"] = reasoning.Id;

                MergeProviderScopedReasoningItemIdMetadata(reasoningMetadata, providerId, reasoning.Id);
                MergeProviderScopedEncryptedContentMetadata(reasoningMetadata, providerId, reasoning.EncryptedContent);
                MergeProviderScopedReasoningSignatureMetadata(reasoningMetadata, providerId, reasoning.EncryptedContent);

                return new AIInputItem
                {
                    Type = "reasoning",
                    Id = reasoning.Id,
                    Content = [.. ToUnifiedReasoningInputContent(reasoning, reasoningMetadata, providerId)],
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

        return MergeConsecutiveUserMessages(MoveProgramOutputsAfterLinkedCalls(result));
    }

    private static List<ResponseInputItem> MoveProgramOutputsAfterLinkedCalls(IReadOnlyList<ResponseInputItem> items)
    {
        var result = new List<ResponseInputItem>(items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not ResponseProgramItem program)
            {
                result.Add(items[index]);
                continue;
            }

            result.Add(program);
            ResponseProgramOutputItem? delayedOutput = null;
            if (index + 1 < items.Count
                && items[index + 1] is ResponseProgramOutputItem candidate
                && string.Equals(candidate.CallId, program.CallId, StringComparison.Ordinal))
            {
                delayedOutput = candidate;
                index++;
            }

            while (index + 1 < items.Count && IsLinkedProgramReplayItem(items[index + 1], program.CallId))
                result.Add(items[++index]);

            if (delayedOutput is not null)
                result.Add(delayedOutput);
        }

        return result;
    }

    private static bool IsLinkedProgramReplayItem(ResponseInputItem item, string programCallId)
        => item switch
        {
            ResponseFunctionCallItem call => string.Equals(call.Caller?.CallerId, programCallId, StringComparison.Ordinal),
            ResponseFunctionCallOutputItem output => string.Equals(output.Caller?.CallerId, programCallId, StringComparison.Ordinal),
            _ => false
        };

    private static IEnumerable<ResponseInputItem> CreateResponsesToolReplayItems(
        AIToolCallContentPart toolPart,
        Dictionary<string, object?> metadata,
        string providerId)
    {
        var callReplayType = ResolveResponsesReplayType(toolPart.Metadata, providerId, "messages.provider.call.metadata");
        var resultReplayType = ResolveResponsesReplayType(toolPart.Metadata, providerId, "messages.provider.result.metadata");

        if (IsProviderExecutedWebSearchToolPart(toolPart, callReplayType, resultReplayType))
        {
            var webSearchCall = CreateValidResponseWebSearchCallItem(toolPart, metadata, providerId);
            if (webSearchCall is not null)
                yield return webSearchCall;
            yield break;
        }

        if (IsCodeInterpreterToolPart(toolPart, callReplayType, resultReplayType))
        {
            var codeInterpreterCall = CreateResponseCodeInterpreterCallItem(toolPart, metadata, providerId);
            if (codeInterpreterCall is not null)
                yield return codeInterpreterCall;
            yield break;
        }

        if (IsProgramToolPart(toolPart) || string.Equals(callReplayType, "program", StringComparison.OrdinalIgnoreCase))
        {
            yield return CreateResponseProgramItem(toolPart, metadata, providerId);
            if (HasToolOutput(toolPart) && string.Equals(resultReplayType, "program_output", StringComparison.OrdinalIgnoreCase))
                yield return CreateResponseProgramOutputItem(toolPart, metadata, providerId);
            yield break;
        }

        if (IsProgramOutputToolPart(toolPart) || string.Equals(resultReplayType, "program_output", StringComparison.OrdinalIgnoreCase))
        {
            yield return CreateResponseProgramOutputItem(toolPart, metadata, providerId);
            yield break;
        }

        if (IsToolSearchCallPart(toolPart) || string.Equals(callReplayType, "tool_search_call", StringComparison.OrdinalIgnoreCase))
        {
            yield return CreateResponseToolSearchCallItem(toolPart, metadata, providerId);
            if (HasToolOutput(toolPart))
                yield return CreateResponseToolSearchOutputItem(toolPart, metadata, providerId);
            yield break;
        }

        if (IsToolSearchOutputPart(toolPart) || string.Equals(resultReplayType, "tool_search_output", StringComparison.OrdinalIgnoreCase))
        {
            yield return CreateResponseToolSearchOutputItem(toolPart, metadata, providerId);
            yield break;
        }

        yield return CreateResponseFunctionCallItem(toolPart, metadata, providerId);
        if (toolPart.IsClientToolCall && HasToolOutput(toolPart))
            yield return CreateResponseFunctionCallOutputItem(toolPart, metadata, providerId);
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
        var metadata = item.Metadata ?? [];
        var toolParts = (item.Content ?? []).OfType<AIToolCallContentPart>().ToList();
        var reasoningParts = (item.Content ?? []).OfType<AIReasoningContentPart>().ToList();
        var nonToolParts = (item.Content ?? []).Where(a => a is not AIToolCallContentPart && a is not AIReasoningContentPart).ToList();

        if (kind == "message")
        {
            var selectedReasoningParts = SelectReasoningPartsForReplay(reasoningParts, providerId, preferEncryptedReasoningReplay).ToHashSet();
            var pendingMessageParts = new List<AIContentPart>();

            foreach (var part in item.Content ?? [])
            {
                if (part is AIReasoningContentPart reasoningPart)
                {
                    if (pendingMessageParts.Count > 0)
                    {
                        yield return CreateResponseInputMessage(item, metadata, pendingMessageParts);
                        pendingMessageParts.Clear();
                    }

                    if (selectedReasoningParts.Contains(reasoningPart))
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

                    continue;
                }

                if (part is not AIToolCallContentPart toolPart)
                {
                    pendingMessageParts.Add(part);
                    continue;
                }

                if (pendingMessageParts.Count > 0)
                {
                    yield return CreateResponseInputMessage(item, metadata, pendingMessageParts);
                    pendingMessageParts.Clear();
                }

                foreach (var replayItem in CreateResponsesToolReplayItems(toolPart, metadata, providerId))
                    yield return replayItem;
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

            if (pendingMessageParts.Count > 0 || ((item.Content?.Count ?? 0) == 0))
                yield return CreateResponseInputMessage(item, metadata, pendingMessageParts);

            yield break;
        }

        switch (kind)
        {
            case "function_call":
                {
                    var toolPart = toolParts.FirstOrDefault();
                    if (toolPart is not null)
                        yield return CreateResponseFunctionCallItem(toolPart, metadata, providerId);
                    yield break;
                }
            case "function_call_output":
                {
                    var toolPart = toolParts.FirstOrDefault();
                    if (toolPart is not null && toolPart.IsClientToolCall && HasToolOutput(toolPart))
                        yield return CreateResponseFunctionCallOutputItem(toolPart, metadata, providerId);
                    yield break;
                }
            case "web_search_call":
                {
                    var toolPart = toolParts.FirstOrDefault();
                    if (toolPart is not null)
                    {
                        var webSearchCall = CreateValidResponseWebSearchCallItem(toolPart, metadata, providerId);
                        if (webSearchCall is not null)
                            yield return webSearchCall;
                    }
                    yield break;
                }
            case "code_interpreter_call":
                {
                    var toolPart = toolParts.FirstOrDefault();
                    if (toolPart is not null)
                    {
                        var codeInterpreterCall = CreateResponseCodeInterpreterCallItem(toolPart, metadata, providerId);
                        if (codeInterpreterCall is not null)
                            yield return codeInterpreterCall;
                    }
                    yield break;
                }
            case "program":
                {
                    var toolPart = toolParts.FirstOrDefault();
                    if (toolPart is not null)
                        yield return CreateResponseProgramItem(toolPart, metadata, providerId);
                    yield break;
                }
            case "program_output":
                {
                    var toolPart = toolParts.FirstOrDefault();
                    if (toolPart is not null)
                        yield return CreateResponseProgramOutputItem(toolPart, metadata, providerId);
                    yield break;
                }
            case "tool_search_call":
                {
                    var toolPart = toolParts.FirstOrDefault();
                    if (toolPart is not null)
                        yield return CreateResponseToolSearchCallItem(toolPart, metadata, providerId);
                    yield break;
                }
            case "tool_search_output":
                {
                    var toolPart = toolParts.FirstOrDefault();
                    if (toolPart is not null)
                        yield return CreateResponseToolSearchOutputItem(toolPart, metadata, providerId);
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
            EncryptedContent = encryptedContent,
        };
    }

    private static IEnumerable<AIContentPart> ToUnifiedReasoningInputContent(
        ResponseReasoningItem reasoning,
        Dictionary<string, object?> reasoningMetadata,
        string providerId)
    {
        if (reasoning.Summary.Count == 0
            && !string.IsNullOrWhiteSpace(reasoning.EncryptedContent))
        {
            yield return new AIReasoningContentPart
            {
                Type = "reasoning",
                Text = string.Empty,
                Signature = reasoning.EncryptedContent,
                Metadata = reasoningMetadata
            };
            yield break;
        }

        foreach (var part in reasoning.Summary)
        {
            var metadata = new Dictionary<string, object?> { ["type"] = part.Type };

            if (!string.IsNullOrWhiteSpace(reasoning.EncryptedContent))
                MergeProviderScopedReasoningSignatureMetadata(metadata, providerId, reasoning.EncryptedContent);

            yield return new AIReasoningContentPart
            {
                Type = "reasoning",
                Text = part.Text,
                Signature = reasoning.EncryptedContent,
                Metadata = metadata
            };
        }
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
            var encryptedContent = ExtractNestedValue<string>(reasoningPart.Metadata ?? [], providerId, "encrypted_content");
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

        // Vercel UI tool invocations preserve provider result metadata under this
        // transport-specific wrapper. Resolve it as provider-scoped metadata too,
        // so Responses replay can recover opaque state such as compaction tokens.
        if (metadata.TryGetValue("messages.provider.metadata", out var nestedProviderMetadata)
            && TryGetJsonObject(nestedProviderMetadata, out var nestedProviderJson)
            && nestedProviderJson.TryGetProperty(providerId, out var nestedProvider)
            && TryGetJsonObject(nestedProvider, out var nestedProviderState)
            && nestedProviderState.TryGetProperty(key, out value))
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

    private static ResponseRole ParseRole(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            "assistant" => ResponseRole.Assistant,
            "system" => ResponseRole.System,
            "developer" => ResponseRole.Developer,
            _ => ResponseRole.User
        };

    private static Dictionary<string, object?> CreateResponsesReplayMetadata(
        string providerId,
        string? type,
        ResponseCaller? caller = null)
    {
        var metadata = new Dictionary<string, object?> { ["responses.type"] = type };
        var providerMetadata = GetOrCreateProviderScopedMetadata(metadata, providerId);
        providerMetadata["type"] = type ?? string.Empty;
        if (caller is not null)
            providerMetadata["caller"] = JsonSerializer.SerializeToElement(caller, Json);
        metadata[providerId] = providerMetadata;
        return metadata;
    }

    private static AIInputItem CreateUnifiedProgramInputItem(ResponseProgramItem program, string providerId)
    {
        var partMetadata = CreateResponsesReplayMetadata(providerId, program.Type);
        var providerMetadata = GetOrCreateProviderScopedMetadata(partMetadata, providerId);
        providerMetadata["id"] = program.Id ?? string.Empty;
        providerMetadata["call_id"] = program.CallId;
        providerMetadata["fingerprint"] = program.Fingerprint;

        return new AIInputItem
        {
            Type = "program",
            Role = "assistant",
            Content = [new AIToolCallContentPart
            {
                Type = "tool-program",
                ToolCallId = program.CallId,
                ToolName = "program",
                Title = "program",
                Input = new { code = program.Code },
                ProviderExecuted = true,
                Metadata = partMetadata
            }],
            Metadata = new Dictionary<string, object?> { [providerId] = providerMetadata }
        };
    }

    private static AIInputItem CreateUnifiedProgramOutputInputItem(ResponseProgramOutputItem output, string providerId)
    {
        var partMetadata = CreateResponsesReplayMetadata(providerId, output.Type);
        var providerMetadata = GetOrCreateProviderScopedMetadata(partMetadata, providerId);
        providerMetadata["id"] = output.Id ?? string.Empty;
        providerMetadata["call_id"] = output.CallId;
        providerMetadata["status"] = output.Status ?? string.Empty;

        return new AIInputItem
        {
            Type = "program_output",
            Role = "tool",
            Content = [new AIToolCallContentPart
            {
                Type = "tool-program-output",
                ToolCallId = output.CallId,
                ToolName = "program",
                Title = "program",
                Output = new { result = output.Result, status = output.Status },
                State = output.Status,
                ProviderExecuted = true,
                Metadata = partMetadata
            }],
            Metadata = new Dictionary<string, object?> { [providerId] = providerMetadata }
        };
    }

    private static bool IsProgramToolPart(AIToolCallContentPart toolPart)
        => string.Equals(toolPart.Type, "tool-program", StringComparison.OrdinalIgnoreCase)
           || string.Equals(ExtractValue<string>(toolPart.Metadata, "responses.type"), "program", StringComparison.OrdinalIgnoreCase);

    private static bool IsProgramOutputToolPart(AIToolCallContentPart toolPart)
        => string.Equals(toolPart.Type, "tool-program-output", StringComparison.OrdinalIgnoreCase)
           || string.Equals(ExtractValue<string>(toolPart.Metadata, "responses.type"), "program_output", StringComparison.OrdinalIgnoreCase);

    private static bool IsToolSearchCallPart(AIToolCallContentPart toolPart)
        => string.Equals(toolPart.Type, "tool-search-call", StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolPart.Type, "tool_search_call", StringComparison.OrdinalIgnoreCase)
           || string.Equals(ExtractValue<string>(toolPart.Metadata, "responses.type"), "tool_search_call", StringComparison.OrdinalIgnoreCase);

    private static bool IsToolSearchOutputPart(AIToolCallContentPart toolPart)
        => string.Equals(toolPart.Type, "tool-search-output", StringComparison.OrdinalIgnoreCase)
           || string.Equals(toolPart.Type, "tool_search_output", StringComparison.OrdinalIgnoreCase)
           || string.Equals(ExtractValue<string>(toolPart.Metadata, "responses.type"), "tool_search_output", StringComparison.OrdinalIgnoreCase);

    private static bool IsProviderExecutedWebSearchToolPart(
        AIToolCallContentPart toolPart,
        string? callReplayType,
        string? resultReplayType)
    {
        if (toolPart.ProviderExecuted != true)
            return false;

        return string.Equals(callReplayType, "web_search_call", StringComparison.OrdinalIgnoreCase)
               || string.Equals(resultReplayType, "web_search_call", StringComparison.OrdinalIgnoreCase)
               || string.Equals(toolPart.ToolName, "web_search", StringComparison.OrdinalIgnoreCase)
               || string.Equals(toolPart.ToolName, "web_search_call", StringComparison.OrdinalIgnoreCase)
               || toolPart.Type.Contains("web_search", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCodeInterpreterToolPart(
        AIToolCallContentPart toolPart,
        string? callReplayType,
        string? resultReplayType)
        => toolPart.ProviderExecuted == true
           && (string.Equals(callReplayType, "code_interpreter_call", StringComparison.OrdinalIgnoreCase)
               || string.Equals(resultReplayType, "code_interpreter_call", StringComparison.OrdinalIgnoreCase)
               || string.Equals(toolPart.Type, "tool-code_interpreter", StringComparison.OrdinalIgnoreCase));

    private static ResponseCodeInterpreterCallItem? CreateResponseCodeInterpreterCallItem(
        AIToolCallContentPart toolPart,
        Dictionary<string, object?> itemMetadata,
        string providerId)
    {
        var callMetadata = ExtractNestedChannelMap(toolPart.Metadata, "messages.provider.call.metadata", providerId);
        var resultMetadata = ExtractNestedChannelMap(toolPart.Metadata, "messages.provider.result.metadata", providerId);
        var directMetadata = toolPart.Metadata is null ? null : ExtractObjectMap(toolPart.Metadata, providerId);
        var itemProviderMetadata = ExtractObjectMap(itemMetadata, providerId);

        object? Read(string key)
            => GetMapValue(resultMetadata, key)
               ?? GetMapValue(callMetadata, key)
               ?? GetMapValue(directMetadata, key)
               ?? GetMapValue(itemProviderMetadata, key);

        var id = Read("item_id")?.ToString()
                 ?? Read("id")?.ToString()
                 ?? toolPart.ToolCallId;
        var code = Read("code")?.ToString() ?? ExtractCodeInterpreterCode(toolPart.Input);
        var containerId = Read("container_id")?.ToString();
        var status = Read("status")?.ToString() ?? toolPart.State ?? "completed";
        var caller = TryConvert<ResponseCaller>(Read("caller"));
        var outputsValue = Read("outputs") ?? ExtractCodeInterpreterOutputs(toolPart.Output);

        if (string.IsNullOrWhiteSpace(id)
            || string.IsNullOrWhiteSpace(containerId)
            || outputsValue is null)
        {
            return null;
        }

        return new ResponseCodeInterpreterCallItem
        {
            Id = id,
            Code = code ?? string.Empty,
            ContainerId = containerId,
            Outputs = JsonSerializer.SerializeToElement(outputsValue, Json),
            Status = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ? "completed" : status,
            Caller = caller
        };
    }

    private static Dictionary<string, object?>? ExtractNestedChannelMap(
        Dictionary<string, object?>? metadata,
        string channel,
        string providerId)
    {
        if (metadata is null || !metadata.TryGetValue(channel, out var channelValue))
            return null;

        return ExtractObjectMap(ToJsonMap(channelValue), providerId);
    }

    private static Dictionary<string, object?>? ExtractObjectMap(
        Dictionary<string, object?> metadata,
        string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
            return null;

        var map = ToJsonMap(value);
        return map.Count == 0 ? null : map;
    }

    private static object? GetMapValue(Dictionary<string, object?>? map, string key)
        => map is not null && map.TryGetValue(key, out var value) && HasMeaningfulValue(value) ? value : null;

    private static string? ExtractCodeInterpreterCode(object? input)
    {
        var inputMap = ToJsonMap(input);
        return GetValue<string>(inputMap, "code") ?? input as string;
    }

    private static object? ExtractCodeInterpreterOutputs(object? output)
    {
        var outputMap = ToJsonMap(output);
        if (outputMap.TryGetValue("outputs", out var outputs))
            return outputs;

        if (outputMap.TryGetValue("structuredContent", out var structuredContent))
        {
            var structuredMap = ToJsonMap(structuredContent);
            if (structuredMap.TryGetValue("outputs", out outputs))
                return outputs;
        }

        return output;
    }

    private static T? TryConvert<T>(object? value) where T : class
    {
        if (value is null)
            return null;
        if (value is T typed)
            return typed;

        try
        {
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json);
        }
        catch
        {
            return null;
        }
    }

    private static ResponseWebSearchCallItem? CreateValidResponseWebSearchCallItem(
        AIToolCallContentPart toolPart,
        Dictionary<string, object?> itemMetadata,
        string providerId)
    {
        var metadata = toolPart.Metadata ?? [];
        var type = ExtractNestedChannelValue<string>(metadata, "messages.provider.call.metadata", providerId, "type")
                   ?? ExtractNestedChannelValue<string>(metadata, "messages.provider.result.metadata", providerId, "type")
                   ?? ExtractNestedValue<string>(metadata, providerId, "type")
                   ?? ExtractNestedValue<string>(itemMetadata, providerId, "type");

        // A web-search-looking UI tool is never converted to a generic function
        // call. Native replay requires explicit metadata from this provider.
        if (!string.Equals(type, "web_search_call", StringComparison.OrdinalIgnoreCase))
            return null;

        var id = ExtractNestedChannelValue<string>(metadata, "messages.provider.call.metadata", providerId, "id")
                 ?? ExtractNestedChannelValue<string>(metadata, "messages.provider.result.metadata", providerId, "id")
                 ?? ExtractNestedValue<string>(metadata, providerId, "id")
                 ?? ExtractNestedValue<string>(itemMetadata, providerId, "id");
        var status = ExtractNestedChannelValue<string>(metadata, "messages.provider.call.metadata", providerId, "status")
                     ?? ExtractNestedChannelValue<string>(metadata, "messages.provider.result.metadata", providerId, "status")
                     ?? ExtractNestedValue<string>(metadata, providerId, "status")
                     ?? ExtractNestedValue<string>(itemMetadata, providerId, "status");
        var action = ExtractNestedChannelValue<JsonElement>(metadata, "messages.provider.call.metadata", providerId, "action");
        if (action.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            action = ExtractNestedChannelValue<JsonElement>(metadata, "messages.provider.result.metadata", providerId, "action");
        if (action.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            action = ExtractNestedValue<JsonElement>(metadata, providerId, "action");
        if (action.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            action = ExtractNestedValue<JsonElement>(itemMetadata, providerId, "action");

        if (string.IsNullOrWhiteSpace(id)
            || !string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            || !IsValidWebSearchAction(action))
        {
            return null;
        }

        return new ResponseWebSearchCallItem
        {
            Id = id,
            Status = "completed",
            Action = action.Clone()
        };
    }

    private static bool IsValidWebSearchAction(JsonElement action)
    {
        if (action.ValueKind != JsonValueKind.Object
            || !action.TryGetProperty("type", out var typeProperty)
            || typeProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return typeProperty.GetString() switch
        {
            "search" => HasNonEmptyWebSearchActionString(action, "query"),
            "open_page" => HasNonEmptyWebSearchActionString(action, "url"),
            "find_in_page" => HasNonEmptyWebSearchActionString(action, "url")
                              && HasNonEmptyWebSearchActionString(action, "pattern"),
            _ => false
        };
    }

    private static bool HasNonEmptyWebSearchActionString(JsonElement action, string propertyName)
        => action.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(property.GetString());

    private static string? ResolveResponsesReplayType(
        Dictionary<string, object?>? metadata,
        string providerId,
        string preferredChannel)
        => metadata is null
            ? null
            : ExtractNestedChannelValue<string>(metadata, preferredChannel, providerId, "type")
              ?? ExtractNestedChannelValue<string>(metadata, "messages.provider.metadata", providerId, "type")
              ?? ExtractNestedValue<string>(metadata, providerId, "type");

    private static T? ExtractNestedChannelValue<T>(
        Dictionary<string, object?> metadata,
        string channel,
        string providerId,
        string key)
    {
        if (metadata.TryGetValue(channel, out var channelValue)
            && TryGetJsonObject(channelValue, out var channelJson)
            && channelJson.TryGetProperty(providerId, out var providerValue)
            && TryGetJsonObject(providerValue, out var providerJson)
            && providerJson.TryGetProperty(key, out var value))
        {
            return value.Deserialize<T>(Json);
        }

        return default;
    }

    private static AIInputItem CreateUnifiedToolSearchCallInputItem(ResponseToolSearchCallItem call, string providerId)
    {
        var metadata = CreateResponsesReplayMetadata(providerId, call.Type);
        var scoped = GetOrCreateProviderScopedMetadata(metadata, providerId);
        scoped["id"] = call.Id ?? string.Empty;
        scoped["execution"] = call.Execution;
        scoped["call_id"] = call.CallId ?? string.Empty;
        scoped["status"] = call.Status ?? string.Empty;
        return new AIInputItem
        {
            Type = "tool_search_call",
            Role = "assistant",
            Content = [new AIToolCallContentPart
            {
                Type = "tool-search-call",
                ToolCallId = call.CallId ?? call.Id ?? string.Empty,
                ToolName = "tool_search",
                Title = "tool_search",
                Input = call.Arguments.Clone(),
                State = call.Status,
                ProviderExecuted = string.Equals(call.Execution, "server", StringComparison.OrdinalIgnoreCase),
                Metadata = metadata
            }],
            Metadata = metadata
        };
    }

    private static AIInputItem CreateUnifiedToolSearchOutputInputItem(ResponseToolSearchOutputItem output, string providerId)
    {
        var metadata = CreateResponsesReplayMetadata(providerId, output.Type);
        var scoped = GetOrCreateProviderScopedMetadata(metadata, providerId);
        scoped["id"] = output.Id ?? string.Empty;
        scoped["execution"] = output.Execution;
        scoped["call_id"] = output.CallId ?? string.Empty;
        scoped["status"] = output.Status ?? string.Empty;
        return new AIInputItem
        {
            Type = "tool_search_output",
            Role = "tool",
            Content = [new AIToolCallContentPart
            {
                Type = "tool-search-output",
                ToolCallId = output.CallId ?? output.Id ?? string.Empty,
                ToolName = "tool_search",
                Title = "tool_search",
                Output = output.Tools,
                State = output.Status,
                ProviderExecuted = string.Equals(output.Execution, "server", StringComparison.OrdinalIgnoreCase),
                Metadata = metadata
            }],
            Metadata = metadata
        };
    }

    private static ResponseToolSearchCallItem CreateResponseToolSearchCallItem(
        AIToolCallContentPart toolPart,
        Dictionary<string, object?> itemMetadata,
        string providerId)
    {
        var metadata = toolPart.Metadata ?? [];
        return new ResponseToolSearchCallItem
        {
            Id = ExtractNestedChannelValue<string>(metadata, "messages.provider.call.metadata", providerId, "id")
                 ?? ExtractNestedValue<string>(metadata, providerId, "id")
                 ?? ExtractNestedValue<string>(itemMetadata, providerId, "id"),
            Execution = ExtractNestedChannelValue<string>(metadata, "messages.provider.call.metadata", providerId, "execution")
                        ?? ExtractNestedValue<string>(metadata, providerId, "execution")
                        ?? (toolPart.ProviderExecuted == true ? "server" : "client"),
            CallId = NormalizeNullableCallId(
                ExtractNestedChannelValue<string>(metadata, "messages.provider.call.metadata", providerId, "call_id")
                ?? ExtractNestedValue<string>(metadata, providerId, "call_id")),
            Status = NormalizeResponsesToolStatus(
                ExtractNestedChannelValue<string>(metadata, "messages.provider.call.metadata", providerId, "status")
                ?? ExtractNestedValue<string>(metadata, providerId, "status")
                ?? toolPart.State,
                hasOutput: HasToolOutput(toolPart)),
            Arguments = JsonSerializer.SerializeToElement(toolPart.Input ?? new { }, Json)
        };
    }

    private static ResponseToolSearchOutputItem CreateResponseToolSearchOutputItem(
        AIToolCallContentPart toolPart,
        Dictionary<string, object?> itemMetadata,
        string providerId)
    {
        var metadata = toolPart.Metadata ?? [];
        return new ResponseToolSearchOutputItem
        {
            Id = ExtractNestedChannelValue<string>(metadata, "messages.provider.result.metadata", providerId, "id")
                 ?? ExtractNestedValue<string>(metadata, providerId, "id")
                 ?? ExtractNestedValue<string>(itemMetadata, providerId, "id"),
            Execution = ExtractNestedChannelValue<string>(metadata, "messages.provider.result.metadata", providerId, "execution")
                        ?? ExtractNestedValue<string>(metadata, providerId, "execution")
                        ?? (toolPart.ProviderExecuted == true ? "server" : "client"),
            CallId = NormalizeNullableCallId(
                ExtractNestedChannelValue<string>(metadata, "messages.provider.result.metadata", providerId, "call_id")
                ?? ExtractNestedValue<string>(metadata, providerId, "call_id")),
            Status = NormalizeResponsesToolStatus(
                ExtractNestedChannelValue<string>(metadata, "messages.provider.result.metadata", providerId, "status")
                ?? ExtractNestedValue<string>(metadata, providerId, "status")
                ?? toolPart.State,
                hasOutput: true),
            Tools = toolPart.Output is null
                ? []
                : JsonSerializer.Deserialize<List<ResponseToolDefinition>>(JsonSerializer.Serialize(toolPart.Output, Json), Json) ?? []
        };
    }

    private static string? NormalizeNullableCallId(string? callId)
        => string.IsNullOrWhiteSpace(callId) ? null : callId;

    private static ResponseProgramItem CreateResponseProgramItem(
        AIToolCallContentPart toolPart,
        Dictionary<string, object?> metadata,
        string providerId)
        => new()
        {
            Id = ExtractNestedChannelValue<string>(toolPart.Metadata ?? [], "messages.provider.call.metadata", providerId, "id")
                 ?? ExtractNestedValue<string>(toolPart.Metadata ?? [], providerId, "id")
                 ?? ExtractNestedValue<string>(metadata, providerId, "id"),
            CallId = ExtractNestedChannelValue<string>(toolPart.Metadata ?? [], "messages.provider.call.metadata", providerId, "call_id")
                     ?? toolPart.ToolCallId,
            Code = ExtractObject<JsonElement>(toolPart.Input is null ? [] : new Dictionary<string, object?> { ["input"] = toolPart.Input }, "input") is { } input
                   && input.ValueKind == JsonValueKind.Object
                   && input.TryGetProperty("code", out var code)
                ? code.GetString() ?? string.Empty
                : ExtractValue<string>(toolPart.Metadata, "code") ?? string.Empty,
            Fingerprint = ExtractNestedChannelValue<string>(toolPart.Metadata ?? [], "messages.provider.call.metadata", providerId, "fingerprint")
                          ?? ExtractNestedValue<string>(toolPart.Metadata ?? [], providerId, "fingerprint")
                          ?? ExtractNestedValue<string>(metadata, providerId, "fingerprint")
                          ?? string.Empty
        };

    private static ResponseProgramOutputItem CreateResponseProgramOutputItem(
        AIToolCallContentPart toolPart,
        Dictionary<string, object?> metadata,
        string providerId)
    {
        var output = toolPart.Output is null ? default : JsonSerializer.SerializeToElement(toolPart.Output, Json);
        return new ResponseProgramOutputItem
        {
            Id = ExtractNestedChannelValue<string>(toolPart.Metadata ?? [], "messages.provider.result.metadata", providerId, "id")
                 ?? ExtractNestedValue<string>(toolPart.Metadata ?? [], providerId, "id")
                 ?? ExtractNestedValue<string>(metadata, providerId, "id"),
            CallId = ExtractNestedChannelValue<string>(toolPart.Metadata ?? [], "messages.provider.result.metadata", providerId, "call_id")
                     ?? toolPart.ToolCallId,
            Result = output.ValueKind == JsonValueKind.Object && output.TryGetProperty("result", out var result)
                ? result.GetString() ?? string.Empty
                : output.ValueKind == JsonValueKind.String ? output.GetString() ?? string.Empty : string.Empty,
            Status = NormalizeResponsesToolStatus(
                ExtractNestedChannelValue<string>(toolPart.Metadata ?? [], "messages.provider.result.metadata", providerId, "status")
                ?? (output.ValueKind == JsonValueKind.Object && output.TryGetProperty("status", out var status) ? status.GetString() : toolPart.State),
                hasOutput: true)
        };
    }

    private sealed record CompactionInvocationState(int ItemIndex, string? ItemId, string EncryptedContent);
}
