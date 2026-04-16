using System.Text.Json;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;


namespace AIHappey.Vercel.Mapping;

public static class VercelUnifiedMapper
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static AIInputItem ToUnifiedInputItem(this UIMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new AIInputItem
        {
            Type = "message",
            Role = message.Role.ToString(),
            Id = message.Id,
            Content = [.. message.Parts.Select(ToUnifiedContentPart).Where(a => a is not null).Select(a => a!)],
            Metadata = message.Metadata?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value)
        };
    }

    public static UIMessage ToUIMessage(AIOutputItem item, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        var role = item.Role?.Trim().ToLowerInvariant() switch
        {
            "system" => Role.system,
            "assistant" => Role.assistant,
            _ => Role.user
        };

        var parts = new List<UIMessagePart>();
        foreach (var part in item.Content ?? [])
        {
            switch (part)
            {
                case AITextContentPart text:
                    parts.Add(new TextUIPart { Text = text.Text });
                    break;

                case AIFileContentPart file:
                    parts.Add(new FileUIPart
                    {
                        MediaType = file.MediaType ?? "application/octet-stream",
                        Filename = file.Filename,
                        Url = file.Data?.ToString() ?? string.Empty
                    });
                    break;

                case AIToolCallContentPart toolCall:
                    var uiPart = ToUIMessagePart(toolCall);
                    if (uiPart is not null)
                        parts.Add(uiPart);
                    break;
            }
        }

        if (parts.Count == 0)
            parts.Add(new TextUIPart { Text = string.Empty });

        return new UIMessage
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Role = role,
            Parts = parts,
            Metadata = ExtractObject<Dictionary<string, object>>(item.Metadata, "vercel.message.metadata")
        };
    }

    public static AIEventEnvelope ToUnifiedEvent(UIMessagePart part)
    {
        ArgumentNullException.ThrowIfNull(part);

        var data = ToDataDictionary(part);

        return new AIEventEnvelope
        {
            Type = $"vercel.ui.{part.Type}",
            Data = data,
            Metadata = new Dictionary<string, object?>
            {
                ["vercel.type"] = part.Type,
                ["vercel.partClr"] = part.GetType().Name
            }
        };
    }

    public static IEnumerable<UIMessagePart> ToUIMessagePart(this AIEventEnvelope envelope, string providerId)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var type = envelope.Type.StartsWith("vercel.ui.", StringComparison.OrdinalIgnoreCase)
            ? envelope.Type["vercel.ui.".Length..]
            : envelope.Type;

        var data = ToObjectMap(envelope.Data);

        if (type is "tool-input-available" or "tool-call")
        {
            var providerExecuted = GetValue<bool?>(data, "providerExecuted");
            var providerMetadata = EnsureProviderExecutedProviderMetadata(
                providerId,
                providerExecuted,
                GetTypedData<AIToolInputAvailableEventData>(envelope)?.ProviderMetadata
                ?? GetTypedData<AIToolInputStartEventData>(envelope)?.ProviderMetadata
                ?? GetNestedProviderMetadata(data));

            yield return new ToolCallPart
            {
                ToolCallId = envelope.Id ?? string.Empty,
                ToolName = GetValue<string>(data, "toolName") ?? "unknown",
                Input = GetValue<object>(data, "input") ?? new { },
                ProviderExecuted = providerExecuted,
                Title = GetValue<string>(data, "title"),
                ProviderMetadata = providerMetadata
            };

            if (providerExecuted == false)
            {
                yield return new ToolApprovalRequestUIPart
                {
                    ApprovalId = envelope.Id ?? string.Empty,
                    ToolCallId = envelope.Id ?? string.Empty
                };
            }

            yield break;
        }


        UIMessagePart part = type switch
        {
            "text" => new TextUIPart
            {
                Text = GetValue<string>(data, "text") ?? string.Empty
            },
            "text-start" => new TextStartUIMessageStreamPart
            {
                Id = envelope.Id ?? string.Empty,
                ProviderMetadata = GetTypedData<AITextStartEventData>(envelope)?.ProviderMetadata
                    ?? GetValue<Dictionary<string, object>>(data, "providerMetadata")
            },
            "text-delta" => new TextDeltaUIMessageStreamPart
            {
                Id = envelope.Id ?? string.Empty,
                Delta = GetTypedData<AITextDeltaEventData>(envelope)?.Delta
                    ?? GetValue<string>(data, "delta")
                    ?? envelope.Data?.ToString() ?? string.Empty,
                ProviderMetadata = GetTypedData<AITextDeltaEventData>(envelope)?.ProviderMetadata
                    ?? GetValue<Dictionary<string, object>>(data, "providerMetadata")
            },
            "text-end" => new TextEndUIMessageStreamPart
            {
                Id = envelope.Id ?? string.Empty,
                ProviderMetadata = GetTypedData<AITextEndEventData>(envelope)?.ProviderMetadata
                    ?? GetValue<Dictionary<string, object>>(data, "providerMetadata")
            },
            "reasoning-start" => new ReasoningStartUIPart
            {
                Id = envelope.Id ?? string.Empty,
                ProviderMetadata = ToLooseProviderMetadata(GetTypedData<AIReasoningStartEventData>(envelope)?.ProviderMetadata)
                    ?? GetReasoningProviderMetadata(data)
                    ?? CreateLegacyReasoningProviderMetadata(
                        providerId,
                        signature: GetValue<string>(data, "signature"),
                        encryptedContent: GetValue<object>(data, "encrypted_content"))
            },
            "reasoning-delta" => new ReasoningDeltaUIPart
            {
                Id = envelope.Id ?? string.Empty,
                Delta = GetTypedData<AIReasoningDeltaEventData>(envelope)?.Delta
                    ?? GetValue<string>(data, "delta")
                    ?? envelope.Data?.ToString() ?? string.Empty,
                ProviderMetadata = ToLooseProviderMetadata(GetTypedData<AIReasoningDeltaEventData>(envelope)?.ProviderMetadata)
                    ?? GetReasoningProviderMetadata(data)
            },
            "reasoning-end" => new ReasoningEndUIPart
            {
                Id = envelope.Id ?? string.Empty,
                ProviderMetadata = GetTypedData<AIReasoningEndEventData>(envelope)?.ProviderMetadata
                    ?? GetNestedProviderMetadata(data)
                    ?? ToNestedProviderMetadata(GetValue<Dictionary<string, object>>(data, "providerMetadata"))
                    ?? CreateLegacyNestedReasoningProviderMetadata(
                        providerId,
                        signature: GetValue<string>(data, "signature"),
                        summary: GetValue<object>(data, "summary"),
                        encryptedContent: GetValue<object>(data, "encrypted_content"))
            },
            "tool-approval-request" => new ToolApprovalRequestUIPart
            {
                ApprovalId = GetValue<string>(data, "approvalId") ?? string.Empty,
                ToolCallId = GetValue<string>(data, "toolCallId") ?? string.Empty
            },
            "tool-input-start" => new ToolCallStreamingStartPart
            {
                ToolCallId = envelope.Id ?? string.Empty,
                ToolName = GetValue<string>(data, "toolName") ?? "unknown",
                ProviderExecuted = GetValue<bool?>(data, "providerExecuted"),
                Title = GetValue<string>(data, "title"),
                ProviderMetadata = EnsureProviderExecutedProviderMetadata(
                    providerId,
                    GetValue<bool?>(data, "providerExecuted"),
                    GetTypedData<AIToolInputStartEventData>(envelope)?.ProviderMetadata
                    ?? GetNestedProviderMetadata(data))
            },
            "tool-input-delta" => new ToolCallDeltaPart
            {
                ToolCallId = envelope.Id ?? string.Empty,
                InputTextDelta = GetValue<string>(data, "inputTextDelta") ?? string.Empty
            },
            "tool-output-available" => new ToolOutputAvailablePart
            {
                ToolCallId = envelope.Id ?? string.Empty,
                Output = WrapToolOutputForUi(
                    GetValue<object>(data, "output"),
                    GetValue<bool?>(data, "providerExecuted")) ?? new { },
                ProviderExecuted = GetValue<bool?>(data, "providerExecuted"),
                Dynamic = GetValue<bool?>(data, "dynamic"),
                Preliminary = GetValue<bool?>(data, "preliminary"),
                ProviderMetadata = EnsureProviderExecutedProviderMetadata(
                    providerId,
                    GetValue<bool?>(data, "providerExecuted"),
                    GetTypedData<AIToolOutputAvailableEventData>(envelope)?.ProviderMetadata
                    ?? GetNestedProviderMetadata(data))
            },
            "tool-output-error" => new ToolOutputErrorPart
            {
                ToolCallId = GetValue<string>(data, "toolCallId") ?? string.Empty,
                ErrorText = GetValue<string>(data, "errorText") ?? string.Empty,
                ProviderExecuted = GetValue<bool?>(data, "providerExecuted"),
                Dynamic = GetValue<bool?>(data, "dynamic"),
                ProviderMetadata = EnsureProviderExecutedProviderMetadata(
                    providerId,
                    GetValue<bool?>(data, "providerExecuted"),
                    GetTypedData<AIToolOutputErrorEventData>(envelope)?.ProviderMetadata
                    ?? GetNestedProviderMetadata(data))
            },
            "source-url" => new SourceUIPart
            {
                SourceId = GetTypedData<AISourceUrlEventData>(envelope)?.SourceId ?? GetValue<string>(data, "sourceId") ?? string.Empty,
                Url = GetTypedData<AISourceUrlEventData>(envelope)?.Url ?? GetValue<string>(data, "url") ?? string.Empty,
                Title = GetTypedData<AISourceUrlEventData>(envelope)?.Title ?? GetValue<string>(data, "title"),
                ProviderMetadata = GetTypedData<AISourceUrlEventData>(envelope)?.ProviderMetadata ?? GetNestedProviderMetadata(data)
            },
            "source-document" => new SourceDocumentPart
            {
                SourceId = GetValue<string>(data, "sourceId") ?? string.Empty,
                MediaType = GetValue<string>(data, "mediaType") ?? "application/octet-stream",
                Filename = GetValue<string>(data, "filename"),
                Title = GetValue<string>(data, "title") ?? string.Empty,
                ProviderMetadata = GetProviderMetadata(data)
            },
            "file" => new FileUIPart
            {
                MediaType = GetTypedData<AIFileEventData>(envelope)?.MediaType
                    ?? GetValue<string>(data, "mediaType")
                    ?? "application/octet-stream",
                Url = GetTypedData<AIFileEventData>(envelope)?.Url
                    ?? GetValue<string>(data, "url")
                    ?? string.Empty,
                //  Filename = GetTypedData<AIFileEventData>(envelope)?.Filename
                //     ?? GetValue<string>(data, "filename"),
                ProviderMetadata = GetTypedData<AIFileEventData>(envelope)?.ProviderMetadata
                    ?? GetDoubleNestedProviderMetadata(data)
            },
            "message-metadata" => new MessageMetadataUIPart
            {
                MessageMetadata = data?.Where(a => a.Value is not null)
                    .ToDictionary(z => z.Key, a => (object)a.Value!) ?? []
            },
            "finish" => new FinishUIPart
            {
                FinishReason = NormalizeFinishReason(GetValue<string>(data, "finishReason") ?? GetTypedData<AIFinishEventData>(envelope)?.FinishReason),
                MessageMetadata = CreateFinishMessageMetadata(envelope, data, providerId)
            },
            "step-start" => new StepStartUIPart(),
            "start-step" => new StartStepUIPart(),
            "error" => new ErrorUIPart
            {
                ErrorText = GetValue<string>(data, "errorText") ?? string.Empty
            },
            "abort" => new AbortUIPart
            {
                Reason = GetValue<string>(data, "reason")
            },
            _ when type.StartsWith("data-", StringComparison.OrdinalIgnoreCase) => new DataUIPart
            {
                Type = type,
                Id = GetTypedData<AIDataEventData>(envelope)?.Id ?? GetValue<string>(data, "id"),
                Data = GetTypedData<AIDataEventData>(envelope)?.Data ?? GetValue<object>(data, "data") ?? new { },
                Transient = GetTypedData<AIDataEventData>(envelope)?.Transient ?? GetValue<bool?>(data, "transient")
            },
            _ when type.StartsWith("tool-", StringComparison.OrdinalIgnoreCase)
                  && type != "tool-call"
                  && !type.StartsWith("tool-input", StringComparison.OrdinalIgnoreCase)
                  && !type.StartsWith("tool-output", StringComparison.OrdinalIgnoreCase) => new ToolInvocationPart
                  {
                      Type = type,
                      ToolCallId = GetValue<string>(data, "toolCallId") ?? string.Empty,
                      Title = GetValue<string>(data, "title"),
                      Input = GetValue<object>(data, "input") ?? new { },
                      State = GetValue<string>(data, "state") ?? string.Empty,
                      Output = WrapToolOutputForUi(
                          GetValue<object>(data, "output"),
                          GetValue<bool?>(data, "providerExecuted")) ?? new { },
                      ProviderExecuted = GetValue<bool?>(data, "providerExecuted"),
                      Approval = GetToolApproval(data)
                  },
            _ => new DataUIPart
            {
                Type = "data-unmapped",
                Data = data
            }
        };

        //if (part.Type != "data-unmapped")
        yield return part;
    }

    private static AIContentPart? ToUnifiedContentPart(UIMessagePart part)
    {
        switch (part)
        {
            case TextUIPart text:
                return new AITextContentPart
                {
                    Type = "text",
                    Text = text.Text,
                    Metadata = new Dictionary<string, object?> { ["vercel.type"] = text.Type }
                };

            case FileUIPart file:
                return new AIFileContentPart
                {
                    Type = "file",
                    MediaType = file.MediaType,
                    Filename = file.Filename,
                    Data = file.Url,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["vercel.type"] = file.Type,
                        ["vercel.providerMetadata"] = file.ProviderMetadata
                    }
                };

            case SourceDocumentPart sourceDocument:
                return new AIFileContentPart
                {
                    Type = "file",
                    MediaType = sourceDocument.MediaType,
                    Filename = sourceDocument.Filename,
                    Data = sourceDocument.Title,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["vercel.type"] = sourceDocument.Type,
                        ["vercel.sourceId"] = sourceDocument.SourceId,
                        ["vercel.providerMetadata"] = sourceDocument.ProviderMetadata
                    }
                };

            case ReasoningUIPart reasoning:
                var reasoningMetadata = reasoning.ProviderMetadata?.ToDictionary(a => a.Key, a => (object?)a.Value)
                                        ?? new Dictionary<string, object?>();

                if (!string.IsNullOrWhiteSpace(reasoning.Id))
                    reasoningMetadata["id"] = reasoning.Id;

                return new AIReasoningContentPart
                {
                    Type = "reasoning",
                    Text = reasoning.Text,
                    Metadata = reasoningMetadata.Count == 0 ? null : reasoningMetadata
                    /*   Metadata = new Dictionary<string, object?>
                       {
                           ["vercel.type"] = reasoning.Type,
                          ["vercel.id"] = reasoning.Id,
                          ["vercel.providerMetadata"] = reasoning.ProviderMetadata
                      }*/
                };

            case ToolInvocationPart invocation:
                return CreateUnifiedToolCallPart(
                    type: invocation.Type,
                    toolCallId: invocation.ToolCallId,
                    title: invocation.Title,
                    input: invocation.Input,
                    state: invocation.State,
                    output: invocation.Output,
                    providerExecuted: invocation.ProviderExecuted,
                    approval: ToUnifiedApproval(invocation.Approval),
                    rawPart: invocation);
        }

        return null;
        /*return new AITextContentPart
        {
            Type = "text",
            Text = JsonSerializer.Serialize(part, part.GetType(), Json),
            Metadata = new Dictionary<string, object?>
            {
                ["vercel.type"] = part.Type,
                ["vercel.unmapped"] = true
            }
        };*/
    }

    private static Dictionary<string, object?> ToDataDictionary(UIMessagePart part)
    {
        var asMap = ToObjectMap(part);
        asMap["type"] = part.Type;
        return asMap;
    }

    private static Dictionary<string, object?> ToObjectMap(object? value)
    {
        if (value is null)
            return new Dictionary<string, object?>();

        if (value is Dictionary<string, object?> dict)
            return dict;

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            return element.EnumerateObject()
                .ToDictionary(a => a.Name, a => (object?)a.Value);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(value, Json), Json)
                   ?? new Dictionary<string, object?>();
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    private static T? GetTypedData<T>(AIEventEnvelope envelope)
    {
        if (envelope.Data is T typed)
            return typed;

        if (envelope.Data is null)
            return default;

        try
        {
            if (envelope.Data is JsonElement json)
                return JsonSerializer.Deserialize<T>(json.GetRawText(), Json);

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(envelope.Data, Json), Json);
        }
        catch
        {
            return default;
        }
    }

    private static Dictionary<string, object>? GetProviderMetadata(Dictionary<string, object?> data)
        => GetValue<Dictionary<string, object>>(data, "providerMetadata");

    private static Dictionary<string, object>? GetReasoningProviderMetadata(Dictionary<string, object?> data)
        => GetValue<Dictionary<string, object>>(data, "providerMetadata")
            ?? ToLooseProviderMetadata(GetNestedProviderMetadata(data));

    private static Dictionary<string, Dictionary<string, object>?>? EnsureProviderExecutedProviderMetadata(
        string providerId,
        bool? providerExecuted,
        Dictionary<string, Dictionary<string, object>>? providerMetadata)
    {
        if (providerExecuted != true)
            return null;

        var normalized = new Dictionary<string, Dictionary<string, object>?>();

        if (providerMetadata is not null)
        {
            foreach (var (key, value) in providerMetadata)
                normalized[key] = value;
        }

        if (!string.IsNullOrWhiteSpace(providerId) && !normalized.ContainsKey(providerId))
            normalized[providerId] = new Dictionary<string, object>();

        return normalized.Count == 0 ? null : normalized;
    }

    private static Dictionary<string, object>? ToLooseProviderMetadata(
        Dictionary<string, Dictionary<string, object>>? providerMetadata)
        => providerMetadata?.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value);

    private static Dictionary<string, Dictionary<string, object>>? ToNestedProviderMetadata(
        Dictionary<string, object>? providerMetadata)
    {
        if (providerMetadata is null || providerMetadata.Count == 0)
            return null;

        var nested = new Dictionary<string, Dictionary<string, object>>();

        foreach (var (providerId, metadata) in providerMetadata)
        {
            switch (metadata)
            {
                case Dictionary<string, object> dict when dict.Count > 0:
                    nested[providerId] = dict;
                    break;
                case Dictionary<string, object?> nullableDict when nullableDict.Count > 0:
                    nested[providerId] = nullableDict
                        .Where(kvp => kvp.Value is not null)
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);
                    break;
                case JsonElement json when json.ValueKind == JsonValueKind.Object:
                    var deserialized = JsonSerializer.Deserialize<Dictionary<string, object>>(json.GetRawText(), Json);
                    if (deserialized is { Count: > 0 })
                        nested[providerId] = deserialized;
                    break;
            }
        }

        return nested.Count == 0 ? null : nested;
    }

    private static Dictionary<string, object>? CreateLegacyReasoningProviderMetadata(
        string providerId,
        string? signature = null,
        object? summary = null,
        object? encryptedContent = null)
        => ToLooseProviderMetadata(
            CreateLegacyNestedReasoningProviderMetadata(providerId, signature, summary, encryptedContent));

    private static Dictionary<string, Dictionary<string, object>>? CreateLegacyNestedReasoningProviderMetadata(
        string providerId,
        string? signature = null,
        object? summary = null,
        object? encryptedContent = null)
    {
        var providerMetadata = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(signature))
            providerMetadata["signature"] = signature;

        if (HasMeaningfulReasoningValue(summary))
            providerMetadata["summary"] = summary!;

        if (HasMeaningfulReasoningValue(encryptedContent))
            providerMetadata["encrypted_content"] = encryptedContent!;

        return providerMetadata.Count == 0
            ? null
            : new Dictionary<string, Dictionary<string, object>>
            {
                [providerId] = providerMetadata
            };
    }

    private static bool HasMeaningfulReasoningValue(object? value)
        => value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            JsonElement json => json.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined,
            _ => true
        };

    // private static string GetSafeProviderId(string? providerId)
    //    => string.IsNullOrWhiteSpace(providerId) ? "unknown" : providerId;

    private static Dictionary<string, Dictionary<string, object>>? GetNestedProviderMetadata(Dictionary<string, object?> data)
        => GetValue<Dictionary<string, Dictionary<string, object>>>(data, "providerMetadata");

    private static Dictionary<string, Dictionary<string, object>?>? GetDoubleNestedProviderMetadata(Dictionary<string, object?> data)
        => GetValue<Dictionary<string, Dictionary<string, object>?>>(data, "providerMetadata");

    private static AIToolCallContentPart CreateUnifiedToolCallPart(
        string type,
        string toolCallId,
        string? title = null,
        object? input = null,
        string? state = null,
        object? output = null,
        bool? providerExecuted = null,
        AIToolCallApproval? approval = null,
        UIMessagePart? rawPart = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["vercel.type"] = type
        };

        if (rawPart is not null)
            metadata["vercel.part.raw"] = JsonSerializer.SerializeToElement(rawPart, rawPart.GetType(), Json);

        if (providerExecuted == true && rawPart is ToolInvocationPart invocation)
            AppendMessagesProviderMetadata(metadata, invocation);

        return new AIToolCallContentPart
        {
            Type = type,
            ToolCallId = toolCallId,
            ToolName = GetToolName(type, title),
            Title = title,
            Input = input,
            State = state,
            Output = UnwrapToolOutputFromUi(output, providerExecuted),
            ProviderExecuted = providerExecuted,
            Approval = approval,
            Metadata = metadata
        };
    }

    private static void AppendMessagesProviderMetadata(
        Dictionary<string, object?> metadata,
        ToolInvocationPart invocation)
    {
        var providerMetadata = NormalizeProviderMetadata(invocation.ResultProviderMetadata)
            ?? NormalizeProviderMetadata(invocation.CallProviderMetadata);

        if (providerMetadata is null || providerMetadata.Count == 0)
            return;

        var providerId = providerMetadata.Keys.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(providerId))
            return;

        metadata["messages.provider.id"] = providerId;
        metadata["messages.provider.metadata"] = providerMetadata;

        if (providerMetadata.TryGetValue(providerId, out var matchedProviderMetadata)
            && matchedProviderMetadata.TryGetValue("type", out var blockType)
            && blockType is not null)
        {
            metadata["messages.block.type"] = blockType.ToString();
        }
    }

    private static Dictionary<string, Dictionary<string, object>>? NormalizeProviderMetadata(
        Dictionary<string, Dictionary<string, object>?>? providerMetadata)
    {
        if (providerMetadata is null)
            return null;

        var normalized = providerMetadata
            .Where(entry => entry.Value is not null && entry.Value.Count > 0)
            .ToDictionary(entry => entry.Key, entry => entry.Value!);

        return normalized.Count == 0 ? null : normalized;
    }

    private static UIMessagePart? ToUIMessagePart(AIToolCallContentPart part)
    {
        return part.Type.StartsWith("tool-", StringComparison.OrdinalIgnoreCase)
            ? new ToolInvocationPart
            {
                Type = part.Type,
                ToolCallId = part.ToolCallId,
                Title = part.Title,
                Input = part.Input ?? new { },
                State = part.State ?? string.Empty,
                Output = WrapToolOutputForUi(part.Output, part.ProviderExecuted) ?? new { },
                ProviderExecuted = part.ProviderExecuted,
                Approval = ToVercelApproval(part.Approval)
            }
            : null;
    }

    private static object? WrapToolOutputForUi(object? output, bool? providerExecuted)
    {
        if (output is null || providerExecuted != true)
            return output;

        if (TryGetCallToolResult(output, out var callToolResult))
            return JsonSerializer.SerializeToElement(CloneWithoutMeta(callToolResult), Json);

        var serializedOutput = SerializeToJsonElement(output);

        var wrapped = serializedOutput.ValueKind switch
        {
            JsonValueKind.String => new ModelContextProtocol.Protocol.CallToolResult
            {
                Content =
                [
                    new ModelContextProtocol.Protocol.TextContentBlock
                    {
                        Text = serializedOutput.GetString() ?? string.Empty
                    }
                ]
            },
            JsonValueKind.Null or JsonValueKind.Undefined => new ModelContextProtocol.Protocol.CallToolResult(),
            _ => new ModelContextProtocol.Protocol.CallToolResult
            {
                StructuredContent = serializedOutput,
                Content = []
            }
        };

        return JsonSerializer.SerializeToElement(wrapped, Json);
    }

    private static object? UnwrapToolOutputFromUi(object? output, bool? providerExecuted)
    {
        if (output is null || providerExecuted != true)
            return output;

        if (!TryGetCallToolResult(output, out var callToolResult))
            return output;

        callToolResult = CloneWithoutMeta(callToolResult);

        if (callToolResult.StructuredContent is JsonElement structuredContent
            && structuredContent.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            && (callToolResult.Content is null || callToolResult.Content.Count == 0)
            && callToolResult.IsError != true)
        {
            return structuredContent.Clone();
        }

        return JsonSerializer.SerializeToElement(callToolResult, Json);
    }

    private static bool TryGetCallToolResult(
        object? output,
        out ModelContextProtocol.Protocol.CallToolResult callToolResult)
    {
        callToolResult = default!;

        if (output is not ModelContextProtocol.Protocol.CallToolResult && !LooksLikeCallToolResult(output))
            return false;

        var candidate = output switch
        {
            ModelContextProtocol.Protocol.CallToolResult ctr => ctr,
            JsonElement json => json.TryDeserialize<ModelContextProtocol.Protocol.CallToolResult>(),
            _ => output.TryDeserialize<ModelContextProtocol.Protocol.CallToolResult>()
        };

        if (candidate is null)
            return false;

        callToolResult = candidate;
        return true;
    }

    private static bool LooksLikeCallToolResult(object? output)
    {
        if (output is null)
            return false;

        if (output is ModelContextProtocol.Protocol.CallToolResult)
            return true;

        var json = output switch
        {
            JsonElement jsonElement => jsonElement,
            _ => SerializeToJsonElement(output)
        };

        if (json.ValueKind != JsonValueKind.Object)
            return false;

        return json.TryGetProperty("structuredContent", out _)
               || json.TryGetProperty("content", out _)
               || json.TryGetProperty("isError", out _)
               || json.TryGetProperty("meta", out _);
    }

    private static ModelContextProtocol.Protocol.CallToolResult CloneWithoutMeta(
        ModelContextProtocol.Protocol.CallToolResult callToolResult)
    {
        callToolResult.Meta = null;
        return callToolResult;
    }

    private static JsonElement SerializeToJsonElement(object output)
        => output switch
        {
            JsonElement json => json.Clone(),
            _ => JsonSerializer.SerializeToElement(output, Json)
        };

    private static string GetToolName(string? type, string? title)
    {
        if (!string.IsNullOrWhiteSpace(title))
            return title;

        if (!string.IsNullOrWhiteSpace(type) && type.StartsWith("tool-", StringComparison.OrdinalIgnoreCase))
            return type["tool-".Length..];

        return "unknown";
    }

    private static AIToolCallApproval? ToUnifiedApproval(ToolApproval? approval)
    {
        if (approval is null)
            return null;

        return new AIToolCallApproval
        {
            Approved = approval.Approved,
            Id = approval.Id,
            Reason = approval.Reason
        };
    }

    private static ToolApproval? ToVercelApproval(AIToolCallApproval? approval)
    {
        if (approval is null)
            return null;

        return new ToolApproval
        {
            Approved = approval.Approved,
            Id = approval.Id ?? string.Empty,
            Reason = approval.Reason
        };
    }

    private static ToolApproval? GetToolApproval(Dictionary<string, object?> data)
    {
        var approval = GetValue<object>(data, "approval");
        if (approval is null)
            return null;

        try
        {
            return approval is ToolApproval toolApproval
                ? toolApproval
                : JsonSerializer.Deserialize<ToolApproval>(JsonSerializer.Serialize(approval, Json), Json);
        }
        catch
        {
            return null;
        }
    }

    private static T? ExtractObject<T>(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return default;

        if (value is T cast)
            return cast;

        try
        {
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json);
        }
        catch
        {
            return default;
        }
    }

    private static T? ExtractValue<T>(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return default;

        if (value is T cast)
            return cast;

        try
        {
            if (value is JsonElement json)
                return JsonSerializer.Deserialize<T>(json.GetRawText(), Json);

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json);
        }
        catch
        {
            return default;
        }
    }

    private static T? GetValue<T>(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
            return default;

        if (value is T cast)
            return cast;

        try
        {
            if (value is JsonElement json)
                return JsonSerializer.Deserialize<T>(json.GetRawText(), Json);

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json);
        }
        catch
        {
            return default;
        }
    }

    private static FinishMessageMetadata CreateFinishMessageMetadata(
        AIEventEnvelope envelope,
        Dictionary<string, object?> data,
        string providerId)
    {
        var typedData = GetTypedData<AIFinishEventData>(envelope);
        var metadata = typedData?.MessageMetadata?.ToDictionary()
            ?? envelope.Metadata?
            .Where(a => a.Value is not null)
            .ToDictionary(a => a.Key, a => a.Value)
            ?? [];

        if (!metadata.ContainsKey("model"))
            metadata["model"] = typedData?.Model ?? GetValue<object>(data, "model");

        metadata["model"] = NormalizeFinishModel(metadata.TryGetValue("model", out var modelValue) ? modelValue : null, providerId);
        metadata["timestamp"] = ResolveFinishTimestamp(metadata.TryGetValue("timestamp", out var timestampValue) ? timestampValue : null, typedData?.CompletedAt ?? GetValue<object>(data, "completed_at"));

        var rawUsage = ResolveRawFinishUsage(typedData, metadata);
        metadata["usage"] = BuildNormalizedFinishUsage(
            typedData?.InputTokens,
            typedData?.OutputTokens,
            typedData?.TotalTokens,
            rawUsage);
        metadata[providerId] = BuildProviderMetadataContainer(
            metadata.TryGetValue(providerId, out var existingProviderMetadata) ? existingProviderMetadata : null,
            rawUsage);

        SetFinishMetadataValue(metadata, "temperature", typedData?.MessageMetadata?.Temperature, data);

        return FinishMessageMetadata.FromDictionary(
            metadata.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value),
            fallbackModel: NormalizeFinishModel(typedData?.Model, providerId),
            fallbackTimestamp: ResolveFinishTimestamp(null, typedData?.CompletedAt));
    }

    private static void SetFinishMetadataValue<T>(Dictionary<string, object?> metadata, string key, T? typedValue, Dictionary<string, object?> data)
    {
        if (HasFinishMetadataValue(metadata, key))
            return;

        if (typedValue is not null)
        {
            metadata[key] = typedValue;
            return;
        }

        metadata[key] = data.TryGetValue(key, out var value) ? value : null;
    }

    private static void SetFinishMetadataValue<T>(Dictionary<string, object?> metadata, string key, T? typedValue)
    {
        if (HasFinishMetadataValue(metadata, key))
            return;

        metadata[key] = typedValue;
    }

    private static Usage BuildNormalizedFinishUsage(
        int? inputTokens,
        int? outputTokens,
        int? totalTokens,
        object? rawUsage)
    {
        inputTokens ??= ExtractUsageInt(rawUsage, "promptTokens", "prompt_tokens", "inputTokens", "input_tokens");
        outputTokens ??= ExtractUsageInt(rawUsage, "completionTokens", "completion_tokens", "outputTokens", "output_tokens");
        totalTokens ??= ExtractUsageInt(rawUsage, "totalTokens", "total_tokens");

        return new Usage
        {
            PromptTokens = inputTokens,
            CompletionTokens = outputTokens,
            TotalTokens = totalTokens ?? ((inputTokens ?? 0) + (outputTokens ?? 0))
        };
    }

    private static object BuildProviderMetadataContainer(object? existingProviderMetadata, object rawUsage)
    {
        var providerMetadata = existingProviderMetadata switch
        {
            JsonElement json when json.ValueKind == JsonValueKind.Object
                => JsonSerializer.Deserialize<Dictionary<string, object?>>(json.GetRawText(), JsonSerializerOptions.Web) ?? [],
            Dictionary<string, object?> dictionary => dictionary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            _ => new Dictionary<string, object?>()
        };

        providerMetadata["usage"] = rawUsage;
        return providerMetadata;
    }

    private static object ResolveRawFinishUsage(AIFinishEventData? typedData, Dictionary<string, object?> metadata)
    {
        if (typedData?.MessageMetadata is { } typedMetadata
            && typedMetadata.Usage.ValueKind == JsonValueKind.Object
            && typedMetadata.Usage.EnumerateObject().Any())
        {
            return typedMetadata.Usage.Clone();
        }

        if (metadata.TryGetValue("usage", out var rawMetadataUsage) && HasUsageObject(rawMetadataUsage))
            return CloneUsageObject(rawMetadataUsage!);

        if (TryExtractUsageFromResponse(typedData?.Response, out var responseUsage))
            return responseUsage;

        if (typedData?.InputTokens is not null || typedData?.OutputTokens is not null || typedData?.TotalTokens is not null)
        {
            return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["inputTokens"] = typedData?.InputTokens,
                ["outputTokens"] = typedData?.OutputTokens,
                ["totalTokens"] = typedData?.TotalTokens ?? ((typedData?.InputTokens ?? 0) + (typedData?.OutputTokens ?? 0))
            }, JsonSerializerOptions.Web);
        }

        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), JsonSerializerOptions.Web);
    }

    private static bool TryExtractUsageFromResponse(object? response, out JsonElement usage)
    {
        usage = default;

        if (response is null)
            return false;

        if (response is JsonElement json)
            return TryExtractUsageFromJson(json, out usage);

        try
        {
            var serializedResponse = JsonSerializer.SerializeToElement(response, JsonSerializerOptions.Web);
            return TryExtractUsageFromJson(serializedResponse, out usage);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractUsageFromJson(JsonElement response, out JsonElement usage)
    {
        usage = default;

        if (response.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var property in response.EnumerateObject())
        {
            if (!string.Equals(property.Name, "usage", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            usage = property.Value.Clone();
            return true;
        }

        return false;
    }

    private static bool HasUsageObject(object? usage)
        => usage switch
        {
            JsonElement json => json.ValueKind == JsonValueKind.Object,
            Dictionary<string, object?> => true,
            _ => false
        };

    private static object CloneUsageObject(object usage)
        => usage switch
        {
            JsonElement json => json.Clone(),
            Dictionary<string, object?> dictionary => dictionary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            _ => JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), JsonSerializerOptions.Web)
        };

    private static int? ExtractUsageInt(object? usage, params string[] keys)
    {
        if (usage is null)
            return null;

        foreach (var key in keys)
        {
            if (TryExtractUsageInt(usage, key, out var value))
                return value;
        }

        return null;
    }

    private static bool TryExtractUsageInt(object usage, string key, out int value)
    {
        value = 0;

        switch (usage)
        {
            case JsonElement json when json.ValueKind == JsonValueKind.Object:
                foreach (var property in json.EnumerateObject())
                {
                    if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt32(out value))
                    {
                        return true;
                    }

                    if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String
                        && int.TryParse(property.Value.GetString(), out value))
                    {
                        return true;
                    }
                }

                return false;
            case Dictionary<string, object?> dictionary:
                return dictionary.TryGetValue(key, out var raw) && TryConvertUsageValue(raw, out value);
            default:
                return false;
        }
    }

    private static bool TryConvertUsageValue(object? raw, out int value)
    {
        value = 0;

        return raw switch
        {
            int intValue => (value = intValue) >= 0 || intValue < 0,
            long longValue when longValue >= int.MinValue && longValue <= int.MaxValue => (value = (int)longValue) >= 0 || longValue < 0,
            string stringValue when int.TryParse(stringValue, out var parsed) => (value = parsed) >= 0 || parsed < 0,
            JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var numericValue) => (value = numericValue) >= 0 || numericValue < 0,
            JsonElement json when json.ValueKind == JsonValueKind.String && int.TryParse(json.GetString(), out var parsedValue) => (value = parsedValue) >= 0 || parsedValue < 0,
            _ => false
        };
    }

    private static bool HasFinishMetadataValue(Dictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
            return false;

        return value is not JsonElement json
            || json.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
    }

    private static string NormalizeFinishReason(string? finishReason)
        => finishReason?.Trim().ToLowerInvariant() switch
        {
            "stop" => "stop",
            "length" => "length",
            "content-filter" => "content-filter",
            "tool-calls" => "tool-calls",
            "error" => "error",
            "other" => "other",
            null or "" => "other",
            _ => "other"
        };

    private static string NormalizeFinishModel(object? model, string providerId)
    {
        var modelText = (model switch
        {
            null => null,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            _ => model.ToString()
        })?.Trim();

        if (string.IsNullOrWhiteSpace(modelText))
            throw new InvalidOperationException("Finish metadata must include a model value.");

        return modelText.Contains('/', StringComparison.Ordinal)
            ? modelText
            : $"{providerId}/{modelText}";
    }

    private static DateTimeOffset ResolveFinishTimestamp(object? timestamp, object? completedAt)
    {
        if (TryParseFinishTimestamp(timestamp, out var explicitTimestamp))
            return explicitTimestamp;

        if (TryParseFinishTimestamp(completedAt, out var completedAtTimestamp))
            return completedAtTimestamp;

        return DateTimeOffset.UtcNow;
    }

    private static bool TryParseFinishTimestamp(object? value, out DateTimeOffset timestamp)
    {
        switch (value)
        {
            case DateTimeOffset dto:
                timestamp = dto;
                return true;
            case DateTime dt:
                timestamp = new DateTimeOffset(dt.ToUniversalTime());
                return true;
            case long l:
                timestamp = DateTimeOffset.FromUnixTimeSeconds(l);
                return true;
            case int i:
                timestamp = DateTimeOffset.FromUnixTimeSeconds(i);
                return true;
            case string s when DateTimeOffset.TryParse(s, out var parsed):
                timestamp = parsed;
                return true;
            case string s when long.TryParse(s, out var unix):
                timestamp = DateTimeOffset.FromUnixTimeSeconds(unix);
                return true;
            case JsonElement json when json.ValueKind == JsonValueKind.String:
                return TryParseFinishTimestamp(json.GetString(), out timestamp);
            case JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt64(out var unixValue):
                timestamp = DateTimeOffset.FromUnixTimeSeconds(unixValue);
                return true;
            default:
                timestamp = default;
                return false;
        }
    }

}

