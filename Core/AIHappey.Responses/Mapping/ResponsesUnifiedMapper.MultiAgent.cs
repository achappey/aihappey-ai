using System.Text.Json;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private const string MultiAgentToolName = "multi_agent";
    private const string OpaqueReplayItemsMetadataKey = "responses.opaque_replay_items";
    private const string OutputOrderMetadataKey = "responses.output_order";

    private static Dictionary<string, Dictionary<string, object>>? CreateNativeItemProviderMetadata(
        string providerId,
        string? type,
        string? itemId,
        int? outputIndex,
        ResponseAgent? agent = null,
        string? action = null,
        string? callId = null,
        string? status = null,
        object? rawItem = null,
        string? role = null,
        string? phase = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["id"] = itemId,
            ["item_id"] = itemId,
            ["output_index"] = outputIndex,
            ["agent"] = agent is null ? null : JsonSerializer.SerializeToElement(agent, Json),
            ["agent_name"] = agent?.AgentName,
            ["action"] = action,
            ["call_id"] = callId,
            ["status"] = status,
            ["responses_item"] = rawItem is null ? null : JsonSerializer.SerializeToElement(rawItem, Json),
            ["role"] = role,
            ["phase"] = phase
        };

        return CreateProviderMetadata(providerId, metadata);
    }

    private static Dictionary<string, object>? ToLooseProviderMetadata(
        Dictionary<string, Dictionary<string, object>>? metadata)
        => metadata?.ToDictionary(entry => entry.Key, entry => (object)entry.Value);

    private static object ParseMultiAgentArguments(JsonElement? arguments)
    {
        if (arguments is not { } value)
            return new { };

        if (value.ValueKind == JsonValueKind.String)
            return ParseJsonString(value.GetString());

        return value.Clone();
    }

    private static object ParseMultiAgentArguments(string? arguments)
        => ParseJsonString(arguments);

    private static object ToMultiAgentOutput(JsonElement? output)
        => output is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } value
            ? value.Clone()
            : Array.Empty<object>();

    private static ResponseAgent? GetStreamAgent(ResponseStreamPart part, ResponseStreamItem? item = null)
        => item?.Agent ?? part.Agent;

    private static ResponseAgent? DeserializeResponseAgent(object? value)
    {
        if (value is null)
            return null;

        try
        {
            return value is JsonElement element
                ? element.Deserialize<ResponseAgent>(Json)
                : JsonSerializer.Deserialize<ResponseAgent>(JsonSerializer.Serialize(value, Json), Json);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object?> AddMultiAgentReplayMetadata(
        Dictionary<string, object?>? source,
        ResponseResult response,
        string providerId)
    {
        var metadata = source?.ToDictionary(entry => entry.Key, entry => entry.Value) ?? [];
        var scoped = GetOrCreateProviderScopedMetadata(metadata, providerId);
        var opaqueItems = new List<Dictionary<string, object?>>();
        var outputOrder = new List<Dictionary<string, object?>>();
        var outputItems = response.Output?.ToList() ?? [];
        var hasMultiAgentCall = outputItems.Any(item => string.Equals(
            GetValue<string>(ToJsonMap(item), "type"),
            "multi_agent_call",
            StringComparison.OrdinalIgnoreCase));

        for (var outputIndex = 0; outputIndex < outputItems.Count; outputIndex++)
        {
            var rawItem = JsonSerializer.SerializeToElement(outputItems[outputIndex], Json);
            if (rawItem.ValueKind != JsonValueKind.Object)
                continue;

            var type = rawItem.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            var id = rawItem.TryGetProperty("id", out var idElement)
                ? idElement.GetString()
                : null;

            outputOrder.Add(new Dictionary<string, object?>
            {
                ["output_index"] = outputIndex,
                ["id"] = id,
                ["type"] = type
            });

            if (string.Equals(type, "agent_message", StringComparison.OrdinalIgnoreCase)
                || (hasMultiAgentCall && string.Equals(type, "message", StringComparison.OrdinalIgnoreCase)))
            {
                opaqueItems.Add(new Dictionary<string, object?>
                {
                    ["output_index"] = outputIndex,
                    ["item"] = rawItem.Clone()
                });
            }
        }

        if (opaqueItems.Count > 0)
            scoped[OpaqueReplayItemsMetadataKey] = JsonSerializer.SerializeToElement(opaqueItems, Json);
        if (outputOrder.Count > 0)
            scoped[OutputOrderMetadataKey] = JsonSerializer.SerializeToElement(outputOrder, Json);
        if (scoped.Count > 0)
            metadata[providerId] = scoped;

        return metadata;
    }

    private static bool IsSubagentMessage(JsonElement item, string? type = null)
    {
        type ??= item.ValueKind == JsonValueKind.Object
                 && item.TryGetProperty("type", out var typeElement)
                 && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;

        if (!string.Equals(type, "message", StringComparison.OrdinalIgnoreCase)
            || item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("agent", out var agent)
            || agent.ValueKind != JsonValueKind.Object
            || !agent.TryGetProperty("agent_name", out var agentName)
            || agentName.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return !string.Equals(agentName.GetString(), "/root", StringComparison.Ordinal);
    }

    private static bool IsRootAgent(ResponseAgent? agent)
    {
        if (agent is null || string.IsNullOrWhiteSpace(agent.AgentName))
            return true;

        var path = agent.AgentName.Trim();
        return path.Count(character => character == '/') <= 1;
    }

    private static IEnumerable<AIEventEnvelope> CreateFallbackSubagentFinalAnswerEnvelopes(
        ResponseResult response,
        string providerId)
    {
        var messages = (response.Output ?? [])
            .Select((item, index) => new
            {
                Item = item,
                Index = index,
                Map = ToJsonMap(item)
            })
            .Where(entry => string.Equals(GetValue<string>(entry.Map, "type"), "message", StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.Equals(GetValue<string>(entry.Map, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
            .Select(entry => new
            {
                entry.Item,
                entry.Index,
                entry.Map,
                Agent = DeserializeResponseAgent(GetValue<object>(entry.Map, "agent")),
                Text = ExtractResponseMessageText(entry.Map),
                Phase = GetValue<string>(entry.Map, "phase")
            })
            .Where(entry => !string.IsNullOrEmpty(entry.Text))
            .ToList();

        var fallback = messages
            .Where(entry => string.Equals(entry.Phase, "final_answer", StringComparison.OrdinalIgnoreCase))
            .LastOrDefault()
            ?? messages.LastOrDefault();
        if (fallback is null)
            yield break;

        var id = GetValue<string>(fallback.Map, "id") ?? $"subagent-final-{fallback.Index}";
        var providerMetadata = ToLooseProviderMetadata(CreateNativeItemProviderMetadata(
            providerId,
            "message",
            id,
            fallback.Index,
            fallback.Agent,
            status: GetValue<string>(fallback.Map, "status"),
            rawItem: fallback.Item,
            role: "assistant",
            phase: fallback.Phase));

        yield return CreateTextStartEnvelope(id, providerMetadata);
        yield return CreateTextDeltaEnvelope(id, fallback.Text, providerMetadata);
        yield return CreateTextEndEnvelope(id, providerMetadata);
    }

    private static string ExtractResponseMessageText(Dictionary<string, object?> map)
    {
        if (!map.TryGetValue("content", out var content)
            || content is not JsonElement contentElement
            || contentElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Concat(contentElement.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object
                           && part.TryGetProperty("type", out var type)
                           && type.ValueKind == JsonValueKind.String
                           && string.Equals(type.GetString(), "output_text", StringComparison.OrdinalIgnoreCase))
            .Select(part => part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null)
            .Where(text => text is not null));
    }

    private static int? FindFallbackSubagentFinalMessageIndex(IReadOnlyList<object> outputItems)
    {
        var hasMultiAgentCall = outputItems.Any(item => string.Equals(
            GetValue<string>(ToJsonMap(item), "type"),
            "multi_agent_call",
            StringComparison.OrdinalIgnoreCase));
        if (!hasMultiAgentCall)
            return null;

        var messages = outputItems
            .Select((item, index) => new
            {
                Index = index,
                Map = ToJsonMap(item)
            })
            .Where(entry => string.Equals(GetValue<string>(entry.Map, "type"), "message", StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.Equals(GetValue<string>(entry.Map, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
            .Select(entry => new
            {
                entry.Index,
                entry.Map,
                Agent = DeserializeResponseAgent(GetValue<object>(entry.Map, "agent")),
                Text = ExtractResponseMessageText(entry.Map),
                Phase = GetValue<string>(entry.Map, "phase")
            })
            .Where(entry => !string.IsNullOrEmpty(entry.Text))
            .ToList();

        return messages
                   .Where(entry => string.Equals(entry.Phase, "final_answer", StringComparison.OrdinalIgnoreCase))
                   .LastOrDefault()?.Index
               ?? messages.LastOrDefault()?.Index;
    }

    private static string GetMultiAgentAction(ResponseStreamItem item)
        => GetAdditionalPropertyValue(item.AdditionalProperties, "action")?.ToString()
           ?? "multi_agent";

    private static string GetMultiAgentCallId(ResponseStreamItem item)
        => item.CallId
           ?? GetAdditionalPropertyValue(item.AdditionalProperties, "call_id")?.ToString()
           ?? item.Id
           ?? Guid.NewGuid().ToString("N");

    private static Dictionary<string, Dictionary<string, object>>? CreateMultiAgentStreamMetadata(
        string providerId,
        ResponseStreamPart part,
        ResponseStreamItem item,
        int outputIndex)
        => CreateNativeItemProviderMetadata(
            providerId,
            item.Type,
            item.Id,
            outputIndex,
            GetStreamAgent(part, item),
            GetMultiAgentAction(item),
            GetMultiAgentCallId(item),
            item.Status,
            item);

    private static void MergeProviderScopedNativeItemMetadata(
        Dictionary<string, object?> metadata,
        string providerId,
        string? type,
        string? itemId,
        int? outputIndex = null,
        ResponseAgent? agent = null,
        string? role = null,
        string? phase = null)
    {
        var scoped = GetOrCreateProviderScopedMetadata(metadata, providerId);
        if (!string.IsNullOrWhiteSpace(type))
            scoped["type"] = type;
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            scoped["id"] = itemId;
            scoped["item_id"] = itemId;
        }
        if (outputIndex is not null)
            scoped["output_index"] = outputIndex.Value;
        if (agent is not null)
        {
            scoped["agent"] = JsonSerializer.SerializeToElement(agent, Json);
            scoped["agent_name"] = agent.AgentName;
        }
        if (!string.IsNullOrWhiteSpace(role))
            scoped["role"] = role;
        if (!string.IsNullOrWhiteSpace(phase))
            scoped["phase"] = phase;
        metadata[providerId] = scoped;
    }

    private static AIInputItem CreateUnifiedMultiAgentCallInputItem(
        ResponseMultiAgentCallItem call,
        string providerId)
    {
        var metadata = CreateResponsesReplayMetadata(providerId, call.Type);
        var scoped = GetOrCreateProviderScopedMetadata(metadata, providerId);
        scoped["id"] = call.Id ?? string.Empty;
        scoped["call_id"] = call.CallId;
        scoped["action"] = call.Action;
        if (call.Agent is not null)
        {
            scoped["agent"] = JsonSerializer.SerializeToElement(call.Agent, Json);
            scoped["agent_name"] = call.Agent.AgentName;
        }
        scoped["responses_item"] = JsonSerializer.SerializeToElement(call, Json);
        metadata[providerId] = scoped;

        return new AIInputItem
        {
            Type = call.Type ?? "multi_agent_call",
            Id = call.Id,
            Role = "assistant",
            Content =
            [
                new AIToolCallContentPart
                {
                    Type = "tool-multi_agent",
                    ToolCallId = call.CallId,
                    ToolName = MultiAgentToolName,
                    Title = call.Action,
                    Input = ParseMultiAgentArguments(call.Arguments),
                    ProviderExecuted = true,
                    Metadata = metadata
                }
            ],
            Metadata = metadata
        };
    }

    private static AIInputItem CreateUnifiedMultiAgentOutputInputItem(
        ResponseMultiAgentCallOutputItem output,
        string providerId)
    {
        var metadata = CreateResponsesReplayMetadata(providerId, output.Type);
        var scoped = GetOrCreateProviderScopedMetadata(metadata, providerId);
        scoped["id"] = output.Id ?? string.Empty;
        scoped["call_id"] = output.CallId;
        scoped["action"] = output.Action;
        if (output.Agent is not null)
        {
            scoped["agent"] = JsonSerializer.SerializeToElement(output.Agent, Json);
            scoped["agent_name"] = output.Agent.AgentName;
        }
        scoped["responses_item"] = JsonSerializer.SerializeToElement(output, Json);
        metadata[providerId] = scoped;

        return new AIInputItem
        {
            Type = output.Type ?? "multi_agent_call_output",
            Id = output.Id,
            Role = "tool",
            Content =
            [
                new AIToolCallContentPart
                {
                    Type = "tool-multi_agent",
                    ToolCallId = output.CallId,
                    ToolName = MultiAgentToolName,
                    Title = output.Action,
                    Output = output.Output.Clone(),
                    ProviderExecuted = true,
                    Metadata = metadata
                }
            ],
            Metadata = metadata
        };
    }

    private static AIInputItem CreateUnifiedOpaqueAgentMessageInputItem(
        ResponseAgentMessageItem message,
        string providerId)
    {
        var metadata = CreateResponsesReplayMetadata(providerId, message.Type);
        var scoped = GetOrCreateProviderScopedMetadata(metadata, providerId);
        scoped["id"] = message.Id ?? string.Empty;
        scoped["author"] = message.Author ?? string.Empty;
        scoped["recipient"] = message.Recipient ?? string.Empty;
        if (message.Agent is not null)
        {
            scoped["agent"] = JsonSerializer.SerializeToElement(message.Agent, Json);
            scoped["agent_name"] = message.Agent.AgentName;
        }
        scoped["responses_item"] = JsonSerializer.SerializeToElement(message, Json);
        metadata[providerId] = scoped;

        return new AIInputItem
        {
            Type = message.Type ?? "agent_message",
            Id = message.Id,
            Metadata = metadata
        };
    }

    private static bool IsMultiAgentToolPart(
        AIToolCallContentPart toolPart,
        string? callReplayType,
        string? resultReplayType)
        => toolPart.ProviderExecuted == true
           && (string.Equals(toolPart.Type, "tool-multi_agent", StringComparison.OrdinalIgnoreCase)
               || string.Equals(toolPart.ToolName, MultiAgentToolName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(callReplayType, "multi_agent_call", StringComparison.OrdinalIgnoreCase)
               || string.Equals(resultReplayType, "multi_agent_call_output", StringComparison.OrdinalIgnoreCase));

    private static ResponseMultiAgentCallItem? CreateResponseMultiAgentCallItem(
        AIToolCallContentPart toolPart,
        Dictionary<string, object?> itemMetadata,
        string providerId)
    {
        var raw = ExtractNestedChannelValue<JsonElement>(
                      toolPart.Metadata ?? [],
                      "messages.provider.call.metadata",
                      providerId,
                      "responses_item");
        if (raw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            raw = ExtractNestedValue<JsonElement>(toolPart.Metadata ?? [], providerId, "responses_item");
        if (TryDeserializeResponseInputItem(raw, out ResponseMultiAgentCallItem? exact))
        {
            // The added event can contain an empty placeholder. Prefer the complete
            // UI tool input when it carries the authoritative done arguments.
            if (string.IsNullOrWhiteSpace(exact.Arguments) && toolPart.Input is not null)
                exact.Arguments = SerializePayload(toolPart.Input, "{}");
            return exact;
        }

        var type = ResolveResponsesReplayType(toolPart.Metadata, providerId, "messages.provider.call.metadata")
                   ?? ResolveResponsesReplayType(itemMetadata, providerId, "messages.provider.call.metadata");
        if (!string.Equals(type, "multi_agent_call", StringComparison.OrdinalIgnoreCase))
            return null;

        return new ResponseMultiAgentCallItem
        {
            Id = ReadMultiAgentMetadata<string>(toolPart, itemMetadata, providerId, "id"),
            CallId = ReadMultiAgentMetadata<string>(toolPart, itemMetadata, providerId, "call_id")
                     ?? toolPart.ToolCallId,
            Action = ReadMultiAgentMetadata<string>(toolPart, itemMetadata, providerId, "action")
                     ?? toolPart.Title
                     ?? MultiAgentToolName,
            Arguments = SerializePayload(toolPart.Input, "{}"),
            Agent = ResolveResponseAgent(null, [toolPart], itemMetadata, providerId)
        };
    }

    private static ResponseMultiAgentCallOutputItem? CreateResponseMultiAgentOutputItem(
        AIToolCallContentPart toolPart,
        Dictionary<string, object?> itemMetadata,
        string providerId)
    {
        var resultRaw = ExtractNestedChannelValue<JsonElement>(
            toolPart.Metadata ?? [],
            "messages.provider.result.metadata",
            providerId,
            "responses_item");
        if (resultRaw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            resultRaw = ExtractNestedChannelValue<JsonElement>(
                toolPart.Metadata ?? [],
                "messages.provider.result.metadata",
                providerId,
                "result_responses_item");
        }
        if (resultRaw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            resultRaw = ExtractNestedValue<JsonElement>(toolPart.Metadata ?? [], providerId, "result_responses_item");
        if (TryDeserializeResponseInputItem(resultRaw, out ResponseMultiAgentCallOutputItem? exact))
            return exact;

        var directRaw = ExtractNestedChannelValue<JsonElement>(
            toolPart.Metadata ?? [],
            "messages.provider.result.metadata",
            providerId,
            "responses_item");
        if (directRaw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            directRaw = ExtractNestedValue<JsonElement>(toolPart.Metadata ?? [], providerId, "responses_item");
        if (TryDeserializeResponseInputItem(directRaw, out exact))
            return exact;

        var type = ResolveResponsesReplayType(toolPart.Metadata, providerId, "messages.provider.result.metadata")
                   ?? ResolveResponsesReplayType(itemMetadata, providerId, "messages.provider.result.metadata");
        if (!string.Equals(type, "multi_agent_call_output", StringComparison.OrdinalIgnoreCase)
            || toolPart.Output is null)
        {
            return null;
        }

        return new ResponseMultiAgentCallOutputItem
        {
            Id = ReadMultiAgentMetadata<string>(toolPart, itemMetadata, providerId, "output_item_id")
                 ?? ReadMultiAgentMetadata<string>(toolPart, itemMetadata, providerId, "id"),
            CallId = ReadMultiAgentMetadata<string>(toolPart, itemMetadata, providerId, "call_id")
                     ?? toolPart.ToolCallId,
            Action = ReadMultiAgentMetadata<string>(toolPart, itemMetadata, providerId, "action")
                     ?? toolPart.Title
                     ?? MultiAgentToolName,
            Output = JsonSerializer.SerializeToElement(toolPart.Output, Json),
            Agent = ResolveResponseAgent(null, [toolPart], itemMetadata, providerId)
        };
    }

    private static T? ReadMultiAgentMetadata<T>(
        AIToolCallContentPart toolPart,
        Dictionary<string, object?> itemMetadata,
        string providerId,
        string key)
        => ExtractNestedChannelValue<T>(toolPart.Metadata ?? [], "messages.provider.call.metadata", providerId, key)
           ?? ExtractNestedChannelValue<T>(toolPart.Metadata ?? [], "messages.provider.result.metadata", providerId, key)
           ?? ExtractNestedValue<T>(toolPart.Metadata ?? [], providerId, key)
           ?? ExtractNestedValue<T>(itemMetadata, providerId, key);

    private static ResponseAgent? ResolveResponseAgent(
        AIInputItem? item,
        IEnumerable<AIContentPart> parts,
        Dictionary<string, object?> itemMetadata,
        string providerId)
    {
        foreach (var part in parts)
        {
            var agent = ExtractNestedValue<ResponseAgent>(part.Metadata ?? [], providerId, "agent");
            if (agent is not null)
                return agent;
        }

        return ExtractNestedValue<ResponseAgent>(itemMetadata, providerId, "agent")
               ?? (!string.IsNullOrWhiteSpace(item?.Metadata is null
                       ? null
                       : ExtractNestedValue<string>(item.Metadata, providerId, "agent_name"))
                   ? new ResponseAgent
                   {
                       AgentName = ExtractNestedValue<string>(item!.Metadata!, providerId, "agent_name")!
                   }
                   : null);
    }

    private static bool TryDeserializeResponseInputItem<T>(JsonElement value, out T? item)
        where T : ResponseInputItem
    {
        item = null;
        if (value.ValueKind != JsonValueKind.Object)
            return false;

        try
        {
            item = value.Deserialize<T>(Json);
            return item is not null;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<ResponseInputItem> MergeOrderedOpaqueReplayItems(
        AIInputItem sourceItem,
        IReadOnlyList<ResponseInputItem> semanticItems,
        string providerId)
    {
        var opaque = ExtractNestedValue<JsonElement>(
            sourceItem.Metadata ?? [],
            providerId,
            OpaqueReplayItemsMetadataKey);
        if (opaque.ValueKind != JsonValueKind.Array || opaque.GetArrayLength() == 0)
            return semanticItems;

        var outputOrder = ExtractNestedValue<JsonElement>(
            sourceItem.Metadata ?? [],
            providerId,
            OutputOrderMetadataKey);
        var orderById = new Dictionary<string, int>(StringComparer.Ordinal);
        if (outputOrder.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in outputOrder.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || !entry.TryGetProperty("output_index", out var index)
                    || !index.TryGetInt32(out var outputIndex)
                    || string.IsNullOrWhiteSpace(id.GetString()))
                {
                    continue;
                }

                orderById[id.GetString()!] = outputIndex;
            }
        }

        var ordered = new List<(int Index, int StableIndex, ResponseInputItem Item)>();
        var stableIndex = 0;
        var semanticIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var semanticItem in semanticItems)
        {
            var id = GetResponseInputItemId(semanticItem);
            if (!string.IsNullOrWhiteSpace(id))
                semanticIds.Add(id);
            ordered.Add((id is not null && orderById.TryGetValue(id, out var index) ? index : int.MaxValue, stableIndex++, semanticItem));
        }

        foreach (var entry in opaque.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("output_index", out var index)
                || !index.TryGetInt32(out var outputIndex)
                || !entry.TryGetProperty("item", out var rawItem)
                || !TryDeserializeResponseInputItem(rawItem, out ResponseInputItem? replayItem))
            {
                continue;
            }

            var replayId = GetResponseInputItemId(replayItem!);
            if (!string.IsNullOrWhiteSpace(replayId) && semanticIds.Contains(replayId))
                continue;

            ordered.Add((outputIndex, stableIndex++, replayItem!));
        }

        return ordered
            .OrderBy(entry => entry.Index)
            .ThenBy(entry => entry.StableIndex)
            .Select(entry => entry.Item)
            .ToList();
    }

    private static string? GetResponseInputItemId(ResponseInputItem item)
        => item switch
        {
            ResponseInputMessage message => message.Id,
            ResponseReasoningItem reasoning => reasoning.Id,
            ResponseFunctionCallItem call => call.Id,
            ResponseFunctionCallOutputItem output => output.Id,
            ResponseMultiAgentCallItem call => call.Id,
            ResponseMultiAgentCallOutputItem output => output.Id,
            ResponseAgentMessageItem message => message.Id,
            ResponseWebSearchCallItem webSearch => webSearch.Id,
            ResponseCodeInterpreterCallItem codeInterpreter => codeInterpreter.Id,
            ResponseProgramItem program => program.Id,
            ResponseProgramOutputItem output => output.Id,
            ResponseToolSearchCallItem toolSearch => toolSearch.Id,
            ResponseToolSearchOutputItem output => output.Id,
            _ => null
        };

    private static string? NormalizeNativeResponseMessageId(string? id)
        => !string.IsNullOrWhiteSpace(id)
           && id.StartsWith("msg_", StringComparison.Ordinal)
            ? id
            : null;

    private static bool TryDeserializeResponseInputItem(JsonElement value, out ResponseInputItem? item)
    {
        item = null;
        if (value.ValueKind != JsonValueKind.Object)
            return false;

        try
        {
            item = value.Deserialize<ResponseInputItem>(ResponseJson.Default);
            return item is not null;
        }
        catch
        {
            return false;
        }
    }
}
