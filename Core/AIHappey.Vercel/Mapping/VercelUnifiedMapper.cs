using System.Text.Json;
using AIHappey.Unified.Models;
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
            Content = message.Parts.Select(ToUnifiedContentPart).Where(a => a is not null).Select(a => a!).ToList(),
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

            yield return new ToolCallPart
            {
                ToolCallId = envelope.Id ?? string.Empty,
                ToolName = GetValue<string>(data, "toolName") ?? "unknown",
                Input = GetValue<object>(data, "input") ?? new { },
                ProviderExecuted = providerExecuted,
                Title = GetValue<string>(data, "title")
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
                Title = GetValue<string>(data, "title")
            },
            "tool-input-delta" => new ToolCallDeltaPart
            {
                ToolCallId = envelope.Id ?? string.Empty,
                InputTextDelta = GetValue<string>(data, "inputTextDelta") ?? string.Empty
            },
            "tool-output-available" => new ToolOutputAvailablePart
            {
                ToolCallId = envelope.Id ?? string.Empty,
                Output = GetValue<object>(data, "output") ?? new { },
                ProviderExecuted = GetValue<bool?>(data, "providerExecuted"),
                Dynamic = GetValue<bool?>(data, "dynamic"),
                Preliminary = GetValue<bool?>(data, "preliminary")
            },
            "tool-output-error" => new ToolOutputErrorPart
            {
                ToolCallId = GetValue<string>(data, "toolCallId") ?? string.Empty,
                ErrorText = GetValue<string>(data, "errorText") ?? string.Empty,
                ProviderExecuted = GetValue<bool?>(data, "providerExecuted"),
                Dynamic = GetValue<bool?>(data, "dynamic")
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
                MediaType = GetValue<string>(data, "mediaType") ?? "application/octet-stream",
                Url = GetValue<string>(data, "url") ?? string.Empty,
                Filename = GetValue<string>(data, "filename"),
                ProviderMetadata = GetDoubleNestedProviderMetadata(data)
            },
            "message-metadata" => new MessageMetadataUIPart
            {
                MessageMetadata = data?.Where(a => a.Value is not null)
                    .ToDictionary(z => z.Key, a => (object)a.Value!) ?? []
            },
            "finish" => new FinishUIPart
            {
                FinishReason = GetValue<string>(data, "finishReason"),
                MessageMetadata = (
           envelope?.Metadata?
               .Where(a => a.Value is not null)
               .ToDictionary(a => a.Key, a => (object)a.Value!)
           ?? []
       ) is var meta
           ? meta.Concat(
               new[]
               {
                !meta.ContainsKey("timestamp")
                    ? new KeyValuePair<string, object>(
                        "timestamp",
                        GetValue<object>(data, "completed_at") switch
                        {
                            long l => DateTimeOffset.FromUnixTimeSeconds(l).UtcDateTime.ToString("O"),
                            int i => DateTimeOffset.FromUnixTimeSeconds(i).UtcDateTime.ToString("O"),
                            string s when long.TryParse(s, out var l)
                                => DateTimeOffset.FromUnixTimeSeconds(l).UtcDateTime.ToString("O"),
                            _ => DateTime.UtcNow.ToString("O")
                        })
                    : default,

                !meta.ContainsKey("model") && GetValue<object>(data, "model") is { } model
                    ? new KeyValuePair<string, object>(
                        "model",
                        $"{providerId}/{model}")
                    : default,

                !meta.ContainsKey("inputTokens") && GetValue<object>(data, "inputTokens") is { } it
                    ? new KeyValuePair<string, object>("inputTokens", it)
                    : default,

                !meta.ContainsKey("outputTokens") && GetValue<object>(data, "outputTokens") is { } ot
                    ? new KeyValuePair<string, object>("outputTokens", ot)
                    : default,

                !meta.ContainsKey("totalTokens") && GetValue<object>(data, "totalTokens") is { } tt
                    ? new KeyValuePair<string, object>("totalTokens", tt)
                    : default
               }
               .Where(kv => !kv.Equals(default)))
               .ToDictionary(k => k.Key, v => v.Value)
           : []
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
                      Output = GetValue<object>(data, "output") ?? new { },
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
                return new AIReasoningContentPart
                {
                    Type = "reasoning",
                    Text = reasoning.Text,
                    Metadata = reasoning.ProviderMetadata?.ToDictionary(a => a.Key, a => (object?)a.Value)
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

        return new AIToolCallContentPart
        {
            Type = type,
            ToolCallId = toolCallId,
            ToolName = GetToolName(type, title),
            Title = title,
            Input = input,
            State = state,
            Output = output,
            ProviderExecuted = providerExecuted,
            Approval = approval,
            Metadata = metadata
        };
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
                Output = part.Output ?? new { },
                ProviderExecuted = part.ProviderExecuted,
                Approval = ToVercelApproval(part.Approval)
            }
            : null;
    }

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

}

