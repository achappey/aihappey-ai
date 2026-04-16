using System.Text.Json;

namespace AIHappey.Messages.Mapping;

public static partial class MessagesUnifiedMapper
{


    public static void ApplyProviderOptions(
    this string provider,
    Dictionary<string, object?>? metadata,
    IDictionary<string, JsonElement>? additional, HashSet<string>? exclude = null)
    {
        if (metadata is null || additional is null)
            return;

        if (!metadata.TryGetValue(provider, out var obj))
            return;

        if (obj is not JsonElement json)
            return;

        foreach (var prop in json.EnumerateObject())
        {
            if (exclude?.Contains(prop.Name) == true)
                continue;

            additional[prop.Name] = prop.Value;
        }
    }


    public static List<MessageToolDefinition>? GetMessageToolDefinitions(
     this Dictionary<string, object?>? metadata,
     string providerId)
    {
        if (metadata is null)
            return null;

        if (!metadata.TryGetValue(providerId, out var providerObj) || providerObj is null)
            return null;

        JsonElement providerJson;

        try
        {
            providerJson = providerObj switch
            {
                JsonElement je when je.ValueKind == JsonValueKind.Object => je,
                _ => JsonSerializer.SerializeToElement(providerObj, JsonSerializerOptions.Web)
            };
        }
        catch
        {
            return null;
        }

        if (!providerJson.TryGetProperty("tools", out var toolsEl) ||
            toolsEl.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<MessageToolDefinition>();

        foreach (var toolEl in toolsEl.EnumerateArray())
        {
            try
            {
                var def = toolEl.Deserialize<MessageToolDefinition>(JsonSerializerOptions.Web);
                if (def is not null)
                    result.Add(def);
            }
            catch
            {
                // ignore invalid entries
            }
        }

        return result.Count > 0 ? result : null;
    }

    public static string? StripBase64Prefix(this string? value) =>
    value is null ? null :
    (value.Contains(',') ? value[(value.IndexOf(',') + 1)..] : value);

    private static bool IsToolInputBlock(string? type)
        => type is "tool_use" or "server_tool_use" or "mcp_tool_use";

    private static bool IsProviderExecutedTool(string? type)
        => type is "server_tool_use" or "mcp_tool_use";

    private static bool IsToolOutputBlock(string? type)
        => type is "tool_result"
            or "mcp_tool_result"
            or "web_search_tool_result"
            or "web_fetch_tool_result"
            or "code_execution_tool_result"
            or "bash_code_execution_tool_result"
            or "text_editor_code_execution_tool_result"
            or "tool_search_tool_result";

    private static string NormalizeRole(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            "assistant" => "assistant",
            _ => "user"
        };

    private static string? FlattenContentText(MessagesContent? content)
    {
        if (content is null)
            return null;

        if (content.IsText)
            return content.Text;

        var texts = (content.Blocks ?? [])
            .Where(a => a.Type == "text" && !string.IsNullOrWhiteSpace(a.Text))
            .Select(a => a.Text)
            .ToList();

        return texts.Count == 0 ? null : string.Join("\n\n", texts);
    }

    private static string ToUnifiedStatus(string? stopReason)
        => stopReason?.Trim().ToLowerInvariant() switch
        {
            "refusal" => "filtered",
            "pause_turn" => "in_progress",
            _ => "completed"
        };

    private static string? ToMessagesStopReason(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "filtered" => "refusal",
            "in_progress" => "pause_turn",
            _ => "end_turn"
        };

    private static string ToUiFinishReason(string? stopReason)
        => stopReason?.Trim().ToLowerInvariant() switch
        {
            "tool_use" => "tool-calls",
            "max_tokens" => "length",
            "stop_sequence" => "stop",
            "pause_turn" => "other",
            "refusal" => "content-filter",
            _ => "stop"
        };

    private static string? GuessMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var end = value.IndexOf(';');
            if (end > 5)
                return value[5..end];
        }

        return null;
    }

    private static T? ExtractObject<T>(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return default;

        return DeserializeFromObject<T>(value);
    }

    public static T? GetProviderOption<T>(
           this Dictionary<string, object?>? metadata,
           string providerId,
           string key)
    {
        if (metadata is null)
            return default;

        if (!metadata.TryGetValue(providerId, out var providerObj) || providerObj is null)
            return default;

        var provider = providerObj as Dictionary<string, object?>
            ?? DeserializeFromObject<Dictionary<string, object?>>(providerObj);

        if (provider is null)
            return default;

        if (!provider.TryGetValue(key, out var value) || value is null)
            return default;

        try
        {
            if (value is T cast)
                return cast;

            // fallback for loose types (e.g. JsonElement, boxed primitives)
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value));
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

        return DeserializeFromObject<T>(value);
    }

    private static bool TryGetMatchingProviderMetadata(
        Dictionary<string, object?>? metadata,
        out Dictionary<string, object>? providerMetadata)
    {
        providerMetadata = null;

        var providerId = ExtractValue<string>(metadata, "messages.provider.id");
        if (string.IsNullOrWhiteSpace(providerId))
            return false;

        var nested = ExtractObject<Dictionary<string, Dictionary<string, object>>>(metadata, "messages.provider.metadata");
        if (nested is null || !nested.TryGetValue(providerId, out var matchedProviderMetadata) || matchedProviderMetadata.Count == 0)
            return false;

        providerMetadata = matchedProviderMetadata;
        return true;
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

    private static JsonElement? SerializeToNullableElement(object? value)
    {
        if (value is null)
            return null;

        try
        {
            return value is JsonElement json ? json.Clone() : JsonSerializer.SerializeToElement(value, Json);
        }
        catch
        {
            return null;
        }
    }
}
