using System.Collections.Concurrent;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Interactions.Mapping;

public static partial class InteractionsUnifiedMapper
{
    private static readonly ConcurrentDictionary<string, string> StreamContentTypes = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> StreamThoughtSignatures = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> StreamThoughtHasText = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> StreamTextStarts = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> StreamReasoningStarts = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> StreamOpenThoughtAnchors = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, InteractionStreamImageState> StreamImages = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, InteractionStreamVideoState> StreamVideos = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, InteractionStreamFunctionCallState> StreamFunctionCalls = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, InteractionStreamToolStepState> StreamToolSteps = new(StringComparer.Ordinal);


    public static Dictionary<string, object?>? ToDictionary(this object? obj)
    {
        if (obj is null) return null;

        var json = JsonSerializer.Serialize(obj);
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
    }

    private static T? ExtractObject<T>(Dictionary<string, object?>? metadata, string key)
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

    private static Dictionary<string, object?>? GetProviderScopedMetadata(
        Dictionary<string, object?>? metadata,
        string providerId)
    {
        if (metadata is null
            || string.IsNullOrWhiteSpace(providerId)
            || !metadata.TryGetValue(providerId, out var scopedValue)
            || scopedValue is null)
        {
            return null;
        }

        return scopedValue switch
        {
            Dictionary<string, object?> scoped => scoped,
            JsonElement json when json.ValueKind == JsonValueKind.Object => json.EnumerateObject()
                .ToDictionary(a => a.Name, a => (object?)a.Value.Clone()),
            _ => null
        };
    }

    private static Dictionary<string, Dictionary<string, object>>? GetProviderScopedMetadataEnvelope(
        Dictionary<string, object?>? metadata,
        string providerId)
    {
        var scoped = GetProviderScopedMetadata(metadata, providerId);
        if (scoped is null || scoped.Count == 0)
            return null;

        var normalized = scoped
            .Where(a => a.Value is not null)
            .ToDictionary(a => a.Key, a => ConvertProviderMetadataValue(a.Value)!);

        return normalized.Count == 0
            ? null
            : new Dictionary<string, Dictionary<string, object>>
            {
                [providerId] = normalized
            };
    }

    private static T? ExtractProviderScopedValue<T>(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || metadata.Count == 0)
            return default;

        foreach (var value in metadata.Values)
        {
            var nested = value switch
            {
                Dictionary<string, object?> dict => dict,
                JsonElement json when json.ValueKind == JsonValueKind.Object => json.EnumerateObject()
                    .ToDictionary(a => a.Name, a => (object?)a.Value.Clone()),
                _ => null
            };

            if (nested is null || !nested.TryGetValue(key, out var nestedValue) || nestedValue is null)
                continue;

            if (nestedValue is T cast)
                return cast;

            try
            {
                if (nestedValue is JsonElement json)
                    return JsonSerializer.Deserialize<T>(json.GetRawText(), Json);

                return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(nestedValue, Json), Json);
            }
            catch
            {
                return default;
            }
        }

        return default;
    }

    private static object? ConvertProviderMetadataValue(object? value)
        => value switch
        {
            null => null,
            JsonElement json => json.Clone(),
            _ => value
        };

    private static Dictionary<string, object?> CreateToolProviderMetadata(
        string? signature = null,
        bool? isError = null,
        string? serverName = null,
        string? searchType = null,
        Dictionary<string, JsonElement>? additionalProperties = null)
    {
        var metadata = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(signature))
            metadata["signature"] = signature;

        if (isError is not null)
            metadata["is_error"] = isError.Value;

        if (!string.IsNullOrWhiteSpace(serverName))
            metadata["server_name"] = serverName;

        if (!string.IsNullOrWhiteSpace(searchType))
            metadata["search_type"] = searchType;

        if (additionalProperties is not null)
        {
            foreach (var property in additionalProperties)
                metadata[property.Key] = property.Value.Clone();
        }

        return metadata;
    }

    private static Dictionary<string, object?> ToJsonMap(object? value)
    {
        if (value is null)
            return [];

        if (value is Dictionary<string, object?> dict)
            return dict;

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
            return element.EnumerateObject().ToDictionary(a => a.Name, a => (object?)a.Value.Clone());

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(value, Json), Json)
                   ?? [];
        }
        catch
        {
            return [];
        }
    }
    private static T? DeserializeFromCallToolResult<T>(object? value)
    {
        if (value is null)
            return default;

        if (value is T cast)
            return cast;

        try
        {
            // STEP 1: try parse as CallToolResult
            CallToolResult? toolResult = null;

            if (value is JsonElement json)
            {
                toolResult = JsonSerializer.Deserialize<CallToolResult>(json.GetRawText(), Json);
            }
            else
            {
                var serialized = JsonSerializer.Serialize(value, Json);
                toolResult = JsonSerializer.Deserialize<CallToolResult>(serialized, Json);
            }

            // STEP 2: if structuredContent exists → use that
            if (toolResult?.StructuredContent is not null)
            {
                var structured = toolResult.StructuredContent;

                if (structured is JsonElement structuredJson)
                    return JsonSerializer.Deserialize<T>(structuredJson.GetRawText(), Json);

                return JsonSerializer.Deserialize<T>(
                    JsonSerializer.Serialize(structured, Json),
                    Json
                );
            }

            // FALLBACK: original behavior
            if (value is JsonElement fallbackJson)
                return JsonSerializer.Deserialize<T>(fallbackJson.GetRawText(), Json);

            return JsonSerializer.Deserialize<T>(
                JsonSerializer.Serialize(value, Json),
                Json
            );
        }
        catch
        {
            return default;
        }
    }

    private static T? DeserializeFromObject<T>(object? value)
    {
        if (value is null)
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

    private static string NormalizeUnifiedRole(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            "model" => "assistant",
            "agent" => "assistant",
            "assistant" => "assistant",
            "tool" => "tool",
            "system" => "system",
            _ => "user"
        };

    private static string NormalizeInteractionRole(string? role, bool isProviderRole = false)
    {
        var normalized = role?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "assistant" => isProviderRole ? "model" : "model",
            "model" => "model",
            "agent" => "agent",
            "tool" => "user",
            "system" => "user",
            _ => "user"
        };
    }

    private static string SerializePayload(object? value, string fallback = "{}")
        => value switch
        {
            null => fallback,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString() ?? fallback,
            JsonElement json => json.GetRawText(),
            string text => text,
            _ => JsonSerializer.Serialize(value, Json)
        };
 
    private static string? ToJsonString(object? value, string? fallback = null)
        => value switch
        {
            null => fallback,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString() ?? fallback,
            JsonElement json => json.GetRawText(),
            string text => text,
            _ => value.ToString() ?? fallback
        };

    private static JsonElement? CloneJsonElement(JsonElement? value)
        => value is null ? null : value.Value.Clone();

    private static object? CloneIfJsonElement(object? value)
        => value is JsonElement json ? json.Clone() : value;
 
    private static bool IsReasoningOrToolStep(InteractionContent content)
        => content is InteractionThoughtContent
            or InteractionFunctionCallContent
            or InteractionCodeExecutionCallContent
            or InteractionUrlContextCallContent
            or InteractionMcpServerToolCallContent
            or InteractionGoogleSearchCallContent
            or InteractionFileSearchCallContent
            or InteractionGoogleMapsCallContent
            or InteractionFunctionResultContent
            or InteractionCodeExecutionResultContent
            or InteractionUrlContextResultContent
            or InteractionGoogleSearchResultContent
            or InteractionMcpServerToolResultContent
            or InteractionFileSearchResultContent
            or InteractionGoogleMapsResultContent;

    private static bool HasMeaningfulValue(object? value)
        => value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            JsonElement json => json.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined,
            _ => true
        };

    private static bool HasToolOutput(AIToolCallContentPart toolPart)
        => HasMeaningfulValue(toolPart.Output);

    private static bool IsKnownInteractionToolContentType(string? type)
        => type is "function_call"
            or "code_execution_call"
            or "url_context_call"
            or "mcp_server_tool_call"
            or "google_search_call"
            or "file_search_call"
            or "google_maps_call"
            or "function_result"
            or "code_execution_result"
            or "url_context_result"
            or "google_search_result"
            or "mcp_server_tool_result"
            or "file_search_result"
            or "google_maps_result";

    private static string? InferInteractionToolContentType(AIToolCallContentPart tool)
    {
        if (IsKnownInteractionToolContentType(tool.Type))
            return tool.Type;

        var metadataType = ExtractValue<string>(tool.Metadata, "interactions.content.type")
                           ?? ExtractProviderScopedValue<string>(tool.Metadata, "type");
        if (IsKnownInteractionToolContentType(metadataType))
            return metadataType;

        return InferInteractionToolContentType(tool.ToolName, tool.ProviderExecuted, HasToolOutput(tool));
    }

    private static string InferInteractionToolContentType(
        string? toolName,
        bool? providerExecuted,
        bool hasOutput)
    {
        if (hasOutput)
        {
            if (providerExecuted != true)
                return "function_result";

            return toolName switch
            {
                "code_execution" => "code_execution_result",
                "url_context" => "url_context_result",
                "google_search" => "google_search_result",
                "file_search" => "file_search_result",
                "google_maps" => "google_maps_result",
                _ => "mcp_server_tool_result"
            };
        }

        if (providerExecuted != true)
            return "function_call";

        return toolName switch
        {
            "code_execution" => "code_execution_call",
            "url_context" => "url_context_call",
            "google_search" => "google_search_call",
            "file_search" => "file_search_call",
            "google_maps" => "google_maps_call",
            _ => "mcp_server_tool_call"
        };
    }

    private static string FlattenContentText(IEnumerable<InteractionContent>? content)
        => string.Join("\n", (content ?? []).OfType<InteractionTextContent>().Select(a => a.Text).Where(a => !string.IsNullOrWhiteSpace(a))!);

    private static bool IsHttpUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, Dictionary<string, object>>? CreateProviderScopedMetadata(string providerId, Dictionary<string, object>? payload)
        => payload is null ? null : new Dictionary<string, Dictionary<string, object>> { [providerId] = payload };

    private static string BuildContentEventId(int index)
        => $"interactions-content-{index}";

    private static int GetContentIndex(AIStreamEvent streamEvent)
        => ExtractValue<int?>(streamEvent.Metadata, "interactions.content.index") ?? 0;

    private static string BuildStreamContentKey(string providerId, int index)
        => $"{providerId}:{index}";

    private static void ResetStreamState(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return;

        RemoveByProviderPrefix(StreamContentTypes, providerId);
        RemoveByProviderPrefix(StreamThoughtSignatures, providerId);
        RemoveByProviderPrefix(StreamThoughtHasText, providerId);
        RemoveByProviderPrefix(StreamTextStarts, providerId);
        RemoveByProviderPrefix(StreamReasoningStarts, providerId);
        RemoveByProviderPrefix(StreamImages, providerId);
        RemoveByProviderPrefix(StreamVideos, providerId);
        RemoveByProviderPrefix(StreamFunctionCalls, providerId);
        RemoveByProviderPrefix(StreamToolSteps, providerId);
        StreamOpenThoughtAnchors.TryRemove(providerId, out _);
    }

    private static void RemoveByProviderPrefix<T>(ConcurrentDictionary<string, T> dictionary, string providerId)
    {
        var prefix = $"{providerId}:";
        foreach (var key in dictionary.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            dictionary.TryRemove(key, out _);
        }
    }

    private static string BuildImageToolCallId(int index)
        => $"interactions-image-{index}";

    private static string BuildVideoFileId(int index)
        => $"interactions-video-{index}";

    private static void RememberTextStart(string providerId, int index)
        => StreamTextStarts[BuildStreamContentKey(providerId, index)] = true;

    private static bool HasTextStart(string providerId, int index)
        => StreamTextStarts.ContainsKey(BuildStreamContentKey(providerId, index));

    private static void ForgetTextStart(string providerId, int index)
        => StreamTextStarts.TryRemove(BuildStreamContentKey(providerId, index), out _);

    private static void RememberReasoningStart(string providerId, int index)
        => StreamReasoningStarts[BuildStreamContentKey(providerId, index)] = true;

    private static bool HasReasoningStart(string providerId, int index)
        => StreamReasoningStarts.ContainsKey(BuildStreamContentKey(providerId, index));

    private static void ForgetReasoningStart(string providerId, int index)
        => StreamReasoningStarts.TryRemove(BuildStreamContentKey(providerId, index), out _);
 
    private static void RememberStreamFunctionCallStart(string providerId, int index, InteractionFunctionCallContent call)
    {
        StreamFunctionCalls[BuildStreamContentKey(providerId, index)] = new InteractionStreamFunctionCallState
        {
            ToolCallId = call.Id ?? BuildContentEventId(index),
            Name = call.Name ?? "function",
            ArgumentsJson = HasMeaningfulValue(call.Arguments) ? SerializePayload(call.Arguments, string.Empty) : string.Empty
        };
    }
 
    private static InteractionStreamFunctionCallState RememberStreamFunctionCallArgumentsDelta(string providerId, int index, string? argumentsDelta)
        => StreamFunctionCalls.AddOrUpdate(
            BuildStreamContentKey(providerId, index),
            _ => new InteractionStreamFunctionCallState
            {
                ToolCallId = BuildContentEventId(index),
                Name = "function",
                ArgumentsJson = argumentsDelta ?? string.Empty
            },
            (_, existing) => existing with
            {
                ArgumentsJson = ShouldReplaceInitialStreamingArguments(existing.ArgumentsJson, argumentsDelta)
                    ? argumentsDelta ?? string.Empty
                    : (existing.ArgumentsJson ?? string.Empty) + (argumentsDelta ?? string.Empty)
            });
 
    private static bool ShouldReplaceInitialStreamingArguments(string? existingArgumentsJson, string? argumentsDelta)
        => string.Equals(existingArgumentsJson?.Trim(), "{}", StringComparison.Ordinal)
           && !string.IsNullOrWhiteSpace(argumentsDelta)
           && argumentsDelta.TrimStart().StartsWith('{');
 
    private static InteractionStreamFunctionCallState? ForgetStreamFunctionCall(string providerId, int index)
    {
        StreamFunctionCalls.TryRemove(BuildStreamContentKey(providerId, index), out var state);
        return state;
    }

    private static bool HasStreamFunctionCall(string providerId, int index)
        => StreamFunctionCalls.ContainsKey(BuildStreamContentKey(providerId, index));

    private static void RememberStreamToolStep(string providerId, int index, string? toolCallId, string? signature = null)
    {
        if (string.IsNullOrWhiteSpace(toolCallId) && string.IsNullOrWhiteSpace(signature))
            return;

        StreamToolSteps.AddOrUpdate(
            BuildStreamContentKey(providerId, index),
            _ => new InteractionStreamToolStepState
            {
                ToolCallId = toolCallId,
                Signature = signature
            },
            (_, existing) => existing with
            {
                ToolCallId = toolCallId ?? existing.ToolCallId,
                Signature = signature ?? existing.Signature
            });
    }

    private static InteractionStreamToolStepState? GetStreamToolStep(string providerId, int index)
    {
        StreamToolSteps.TryGetValue(BuildStreamContentKey(providerId, index), out var state);
        return state;
    }

    private static InteractionStreamToolStepState? ForgetStreamToolStep(string providerId, int index)
    {
        StreamToolSteps.TryRemove(BuildStreamContentKey(providerId, index), out var state);
        return state;
    }

    private static void RememberStreamImageStart(string providerId, int index, string? mimeType)
    {
        var key = BuildStreamContentKey(providerId, index);
        StreamImages.AddOrUpdate(
            key,
            _ => new InteractionStreamImageState
            {
                ToolCallId = BuildImageToolCallId(index),
                MimeType = mimeType
            },
            (_, existing) => existing with
            {
                ToolCallId = string.IsNullOrWhiteSpace(existing.ToolCallId) ? BuildImageToolCallId(index) : existing.ToolCallId,
                MimeType = mimeType ?? existing.MimeType
            });
    }

    private static InteractionStreamImageState RememberStreamImageDelta(string providerId, int index, string? mimeType, string? data)
    {
        var key = BuildStreamContentKey(providerId, index);
        return StreamImages.AddOrUpdate(
            key,
            _ => new InteractionStreamImageState
            {
                ToolCallId = BuildImageToolCallId(index),
                MimeType = mimeType,
                Data = data
            },
            (_, existing) => existing with
            {
                ToolCallId = string.IsNullOrWhiteSpace(existing.ToolCallId) ? BuildImageToolCallId(index) : existing.ToolCallId,
                MimeType = mimeType ?? existing.MimeType,
                Data = data ?? existing.Data
            });
    }

    private static InteractionStreamImageState? GetStreamImage(string providerId, int index)
        => StreamImages.TryGetValue(BuildStreamContentKey(providerId, index), out var image)
            ? image
            : null;

    private static InteractionStreamImageState? ForgetStreamImage(string providerId, int index)
    {
        StreamImages.TryRemove(BuildStreamContentKey(providerId, index), out var image);
        return image;
    }

    private static void RememberStreamVideoStart(string providerId, int index, string? mimeType, string? data = null, string? uri = null, string? resolution = null)
    {
        var key = BuildStreamContentKey(providerId, index);
        StreamVideos.AddOrUpdate(
            key,
            _ => new InteractionStreamVideoState
            {
                FileId = BuildVideoFileId(index),
                MimeType = mimeType,
                Data = data,
                Uri = uri,
                Resolution = resolution
            },
            (_, existing) => existing with
            {
                FileId = string.IsNullOrWhiteSpace(existing.FileId) ? BuildVideoFileId(index) : existing.FileId,
                MimeType = mimeType ?? existing.MimeType,
                Data = data ?? existing.Data,
                Uri = uri ?? existing.Uri,
                Resolution = resolution ?? existing.Resolution
            });
    }

    private static InteractionStreamVideoState RememberStreamVideoDelta(string providerId, int index, string? mimeType, string? data, string? uri, string? resolution)
    {
        var key = BuildStreamContentKey(providerId, index);
        return StreamVideos.AddOrUpdate(
            key,
            _ => new InteractionStreamVideoState
            {
                FileId = BuildVideoFileId(index),
                MimeType = mimeType,
                Data = data,
                Uri = uri,
                Resolution = resolution
            },
            (_, existing) => existing with
            {
                FileId = string.IsNullOrWhiteSpace(existing.FileId) ? BuildVideoFileId(index) : existing.FileId,
                MimeType = mimeType ?? existing.MimeType,
                Data = data ?? existing.Data,
                Uri = uri ?? existing.Uri,
                Resolution = resolution ?? existing.Resolution
            });
    }

    private static InteractionStreamVideoState? GetStreamVideo(string providerId, int index)
        => StreamVideos.TryGetValue(BuildStreamContentKey(providerId, index), out var video)
            ? video
            : null;

    private static InteractionStreamVideoState? ForgetStreamVideo(string providerId, int index)
    {
        StreamVideos.TryRemove(BuildStreamContentKey(providerId, index), out var video);
        return video;
    }

    private static void RememberStreamThoughtHasText(string providerId, int index, bool hasText)
        => StreamThoughtHasText[BuildStreamContentKey(providerId, index)] = hasText;

    private static bool GetStreamThoughtHasText(string providerId, int index)
        => StreamThoughtHasText.TryGetValue(BuildStreamContentKey(providerId, index), out var hasText) && hasText;

    private static bool ForgetStreamThoughtHasText(string providerId, int index)
    {
        StreamThoughtHasText.TryRemove(BuildStreamContentKey(providerId, index), out var hasText);
        return hasText;
    }

    private static void RememberOpenThoughtAnchor(string providerId, int index)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return;

        StreamOpenThoughtAnchors[providerId] = index;
    }

    private static int? GetOpenThoughtAnchor(string providerId)
        => StreamOpenThoughtAnchors.TryGetValue(providerId, out var index)
            ? index
            : null;

    private static int? ForgetOpenThoughtAnchor(string providerId)
    {
        StreamOpenThoughtAnchors.TryRemove(providerId, out var index);
        return index;
    }

    private static void RememberStreamContentType(string providerId, int index, string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return;

        StreamContentTypes[BuildStreamContentKey(providerId, index)] = type;
    }

    private static string? GetStreamContentType(string providerId, int index)
        => StreamContentTypes.TryGetValue(BuildStreamContentKey(providerId, index), out var type)
            ? type
            : null;

    private static string? ForgetStreamContentType(string providerId, int index)
    {
        StreamContentTypes.TryRemove(BuildStreamContentKey(providerId, index), out var type);
        return type;
    }

    private static void RememberStreamThoughtSignature(string providerId, int index, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return;

        StreamThoughtSignatures[BuildStreamContentKey(providerId, index)] = signature;
    }

    private static string? GetStreamThoughtSignature(string providerId, int index)
        => StreamThoughtSignatures.TryGetValue(BuildStreamContentKey(providerId, index), out var signature)
            ? signature
            : null;

    private static string? ForgetStreamThoughtSignature(string providerId, int index)
    {
        StreamThoughtSignatures.TryRemove(BuildStreamContentKey(providerId, index), out var signature);
        return signature;
    }

    private static string? GetThoughtSignature(InteractionStepDeltaEvent delta)
    {
        if (delta.Delta?.AdditionalProperties is null)
            return null;

        if (!delta.Delta.AdditionalProperties.TryGetValue("signature", out var signature))
            return null;

        return signature.ValueKind == JsonValueKind.String
            ? signature.GetString()
            : signature.GetRawText();
    }

    private static Dictionary<string, Dictionary<string, object>>? CreateThoughtSignatureProviderMetadata(
        string providerId,
        string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        return new Dictionary<string, Dictionary<string, object>>
        {
            [providerId] = new Dictionary<string, object>
            {
                ["type"] = "thought_signature",
                ["signature"] = signature
            }
        };
    }

    private static string? ExtractThoughtSignatureFromProviderMetadata(
        Dictionary<string, Dictionary<string, object>>? providerMetadata,
        string providerId)
    {
        if (providerMetadata is null
            || string.IsNullOrWhiteSpace(providerId)
            || !providerMetadata.TryGetValue(providerId, out var scoped)
            || scoped is null)
        {
            return null;
        }

        if (scoped.TryGetValue("signature", out var rawSignature)
            && !string.IsNullOrWhiteSpace(rawSignature?.ToString()))
        {
            return rawSignature?.ToString();
        }

        return null;
    }

    private static bool TryGetThoughtSignatureProviderMetadata(
        AIReasoningDeltaEventData? data,
        string providerId,
        out string? signature)
    {
        signature = null;

        if (data?.ProviderMetadata is null
            || !data.ProviderMetadata.TryGetValue(providerId, out var providerMetadata)
            || providerMetadata is null)
            return false;

        if (!providerMetadata.TryGetValue("type", out var type)
            || !string.Equals(type?.ToString(), "thought_signature", StringComparison.OrdinalIgnoreCase))
            return false;

        signature = ExtractThoughtSignatureFromProviderMetadata(data.ProviderMetadata, providerId);
        return !string.IsNullOrWhiteSpace(signature);
    }

    private static string? ExtractThoughtSignatureFromProviderMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        foreach (var value in metadata.Values)
        {
            var nested = value switch
            {
                Dictionary<string, object?> dict => dict,
                JsonElement json when json.ValueKind == JsonValueKind.Object => json.EnumerateObject()
                    .ToDictionary(a => a.Name, a => (object?)a.Value.Clone()),
                _ => null
            };

            if (nested is null)
                continue;

            var type = nested.TryGetValue("type", out var typeValue) ? typeValue?.ToString() : null;
            var signature = nested.TryGetValue("signature", out var signatureValue)
                ? signatureValue?.ToString()
                : null;

            if (!string.IsNullOrWhiteSpace(signature)
                && (string.IsNullOrWhiteSpace(type)
                    || string.Equals(type, "thought_signature", StringComparison.OrdinalIgnoreCase)))
            {
                return signature;
            }
        }

        return null;
    }

    public static string StripBase64Prefix(this string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        var commaIndex = input.IndexOf(',');

        // "data:image/png;base64,XXXXX"
        if (commaIndex >= 0 && input[..commaIndex].Contains("base64"))
            return input[(commaIndex + 1)..];

        return input;
    }

    private sealed record InteractionStreamImageState
    {
        public string ToolCallId { get; init; } = string.Empty;

        public string? MimeType { get; init; }

        public string? Data { get; init; }
    }

    private sealed record InteractionStreamVideoState
    {
        public string FileId { get; init; } = string.Empty;

        public string? MimeType { get; init; }

        public string? Data { get; init; }

        public string? Uri { get; init; }

        public string? Resolution { get; init; }
    }
  
    private sealed record InteractionStreamFunctionCallState
    {
        public string ToolCallId { get; init; } = string.Empty;
  
        public string Name { get; init; } = "function";
  
        public string? ArgumentsJson { get; init; }
    }

    private sealed record InteractionStreamToolStepState
    {
        public string? ToolCallId { get; init; }

        public string? Signature { get; init; }
    }
}
