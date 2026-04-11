using System.Collections.Concurrent;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Interactions.Mapping;

public static partial class InteractionsUnifiedMapper
{
    private static readonly ConcurrentDictionary<string, string> StreamContentTypes = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> StreamThoughtSignatures = new(StringComparer.Ordinal);

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

    private static Dictionary<string, object?> ToJsonMap(object? value)
    {
        if (value is null)
            return new Dictionary<string, object?>();

        if (value is Dictionary<string, object?> dict)
            return dict;

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
            return element.EnumerateObject().ToDictionary(a => a.Name, a => (object?)a.Value.Clone());

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

    private static JsonElement? CloneJsonElement(JsonElement? value)
        => value is null ? null : value.Value.Clone();

    private static object? CloneIfJsonElement(object? value)
        => value is JsonElement json ? json.Clone() : value;

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

    private static string FlattenContentText(IEnumerable<InteractionContent>? content)
        => string.Join("\n", (content ?? []).OfType<InteractionTextContent>().Select(a => a.Text).Where(a => !string.IsNullOrWhiteSpace(a))!);

    private static bool IsHttpUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static string? GetInteractionMimeType(AIFileContentPart file)
        => file.MediaType
           ?? ExtractValue<string>(file.Metadata, "interactions.mime_type")
           ?? ExtractValue<string>(file.Metadata, "mime_type");

    private static Dictionary<string, object?> BuildUnifiedRequestMetadata(InteractionRequest request)
    {
        var raw = JsonSerializer.SerializeToElement(request, Json);
        var metadata = new Dictionary<string, object?>
        {
            ["interactions.request.raw"] = raw,
            ["interactions.request.model"] = request.Model,
            ["interactions.request.agent"] = request.Agent,
            ["interactions.request.system_instruction"] = request.SystemInstruction,
            ["interactions.request.response_format"] = CloneIfJsonElement(request.ResponseFormat),
            ["interactions.request.response_mime_type"] = request.ResponseMimeType,
            ["interactions.request.stream"] = request.Stream,
            ["interactions.request.store"] = request.Store,
            ["interactions.request.background"] = request.Background,
            ["interactions.request.generation_config"] = request.GenerationConfig,
            ["interactions.request.agent_config"] = request.AgentConfig,
            ["interactions.request.previous_interaction_id"] = request.PreviousInteractionId,
            ["interactions.request.response_modalities"] = request.ResponseModalities,
            ["interactions.request.service_tier"] = request.ServiceTier,
            ["interactions.request.additional_properties"] = request.AdditionalProperties
        };

        foreach (var prop in raw.EnumerateObject())
            metadata[$"interactions.request.{prop.Name}"] = prop.Value.Clone();

        return metadata;
    }

    private static Dictionary<string, object?> BuildUnifiedResponseMetadata(Interaction response)
    {
        var raw = JsonSerializer.SerializeToElement(response, Json);
        var metadata = new Dictionary<string, object?>
        {
            ["interactions.response.raw"] = raw,
            ["interactions.response.id"] = response.Id,
            ["interactions.response.object"] = response.Object,
            ["interactions.response.created"] = response.Created,
            ["interactions.response.updated"] = response.Updated,
            ["interactions.response.role"] = response.Role,
            ["interactions.response.model"] = response.Model,
            ["interactions.response.agent"] = response.Agent,
            ["interactions.response.system_instruction"] = response.SystemInstruction,
            ["interactions.response.response_format"] = CloneIfJsonElement(response.ResponseFormat),
            ["interactions.response.response_mime_type"] = response.ResponseMimeType,
            ["interactions.response.previous_interaction_id"] = response.PreviousInteractionId,
            ["interactions.response.response_modalities"] = response.ResponseModalities,
            ["interactions.response.service_tier"] = response.ServiceTier,
            ["interactions.response.tools"] = response.Tools,
            ["interactions.response.input"] = response.Input,
            ["interactions.response.generation_config"] = response.GenerationConfig,
            ["interactions.response.agent_config"] = response.AgentConfig,
            ["interactions.response.additional_properties"] = response.AdditionalProperties
        };

        foreach (var prop in raw.EnumerateObject())
            metadata[$"interactions.response.{prop.Name}"] = prop.Value.Clone();

        return metadata;
    }

    private static Dictionary<string, object>? CreateProviderMetadata(string providerId, string type, int index, string? eventId = null)
        => new()
        {
            ["provider"] = providerId,
            ["interactions.content.type"] = type,
            ["interactions.content.index"] = index,
            ["interactions.event_id"] = eventId ?? string.Empty
        };

    private static Dictionary<string, Dictionary<string, object>>? CreateProviderScopedMetadata(string providerId, Dictionary<string, object>? payload)
        => payload is null ? null : new Dictionary<string, Dictionary<string, object>> { [providerId] = payload };

    private static string BuildContentEventId(int index)
        => $"interactions-content-{index}";

    private static int GetContentIndex(AIStreamEvent streamEvent)
        => ExtractValue<int?>(streamEvent.Metadata, "interactions.content.index") ?? 0;

    private static string BuildStreamContentKey(string providerId, int index)
        => $"{providerId}:{index}";

    private static void RememberStreamContentType(string providerId, int index, string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return;

        StreamContentTypes[BuildStreamContentKey(providerId, index)] = type;
    }

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

    private static string? GetThoughtSignature(InteractionContentDeltaEvent delta)
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

        if (!providerMetadata.TryGetValue("signature", out var rawSignature))
            return false;

        signature = rawSignature?.ToString();
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
            var signature = nested.TryGetValue("signature", out var signatureValue) ? signatureValue?.ToString() : null;

            if (!string.IsNullOrWhiteSpace(signature)
                && (string.IsNullOrWhiteSpace(type)
                    || string.Equals(type, "thought_signature", StringComparison.OrdinalIgnoreCase)))
            {
                return signature;
            }
        }

        return null;
    }
}
