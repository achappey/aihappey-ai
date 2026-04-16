using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private const string CompactionToolName = "compaction";

    private static object? ParseJsonString(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch
        {
            return json;
        }
    }

    private static string SerializePayload(object? value, string fallback)
    {
        return value switch
        {
            null => fallback,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString() ?? fallback,
            JsonElement json => json.GetRawText(),
            string text => text,
            _ => JsonSerializer.Serialize(value, Json)
        };
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

    private static T? ExtractValue<T>(JsonElement? metadata, string key)
    {
        if (metadata is null || metadata.Value.ValueKind != JsonValueKind.Object)
            return default;

        if (!metadata.Value.TryGetProperty(key, out var json))
            return default;

        return JsonSerializer.Deserialize<T>(json.GetRawText(), Json);
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

    private static TEnum? ExtractEnum<TEnum>(Dictionary<string, object?>? metadata, string key)
        where TEnum : struct
    {
        var raw = ExtractValue<string>(metadata, key);
        if (string.IsNullOrWhiteSpace(raw))
            return default;

        if (Enum.TryParse<TEnum>(raw, true, out var parsed))
            return parsed;

        return default;
    }

    private static Dictionary<string, object?> ToJsonMap(object? value)
    {
        if (value is null)
            return new Dictionary<string, object?>();

        if (value is Dictionary<string, object?> dict)
            return dict;

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            return element.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value);
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

    private static T GetValue<T>(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
            return default!;

        if (value is T cast)
            return cast;

        try
        {
            if (value is JsonElement json)
                return JsonSerializer.Deserialize<T>(json.GetRawText(), Json)!;

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
        }
        catch
        {
            return default!;
        }
    }

    private static TruncationStrategy? ParseTruncation(Dictionary<string, object?>? metadata, string key)
    {
        var raw = ExtractValue<string>(metadata, key);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.Trim().ToLowerInvariant() switch
        {
            "auto" => TruncationStrategy.Auto,
            "disabled" => TruncationStrategy.Disabled,
            _ => null
        };
    }

    private static bool HasMeaningfulValue(object? value)
        => value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            JsonElement json => json.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined,
            _ => true
        };

    private static object? CloneIfJsonElement(object? value)
        => value is JsonElement json ? json.Clone() : value;

    private static string GetValueAsString(object? value, string fallback = "")
        => value switch
        {
            null => fallback,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString() ?? fallback,
            JsonElement json when json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined => fallback,
            JsonElement json => json.GetRawText(),
            string text => text,
            _ => value.ToString() ?? fallback
        };

    private static Dictionary<string, Dictionary<string, object>>? CreateProviderScopedEncryptedContentMetadata(
        string providerId,
        object? encryptedContent)
    {
        if (!HasMeaningfulValue(encryptedContent))
            return null;

        return new Dictionary<string, Dictionary<string, object>>
        {
            [providerId] = new Dictionary<string, object>
            {
                ["encrypted_content"] = CloneIfJsonElement(encryptedContent)!
            }
        };
    }

    private static void MergeProviderScopedEncryptedContentMetadata(
        Dictionary<string, object?> metadata,
        string providerId,
        object? encryptedContent)
    {
        if (!HasMeaningfulValue(encryptedContent))
            return;

        var providerMetadata = GetOrCreateProviderScopedMetadata(metadata, providerId);
        providerMetadata["encrypted_content"] = CloneIfJsonElement(encryptedContent)!;
        metadata[providerId] = providerMetadata;
    }

    private static void MergeProviderScopedReasoningItemIdMetadata(
        Dictionary<string, object?> metadata,
        string providerId,
        string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        var providerMetadata = GetOrCreateProviderScopedMetadata(metadata, providerId);
        providerMetadata["id"] = itemId;
        providerMetadata["item_id"] = itemId;
        metadata[providerId] = providerMetadata;
    }

    private static Dictionary<string, object> GetOrCreateProviderScopedMetadata(
        Dictionary<string, object?> metadata,
        string providerId)
    {
        if (metadata.TryGetValue(providerId, out var existingProviderMetadata))
        {
            switch (existingProviderMetadata)
            {
                case Dictionary<string, object> providerMetadata:
                    return providerMetadata;
                case JsonElement json when json.ValueKind == JsonValueKind.Object:
                    return JsonSerializer.Deserialize<Dictionary<string, object>>(json.GetRawText(), Json) ?? [];
                case not null:
                    try
                    {
                        return JsonSerializer.Deserialize<Dictionary<string, object>>(
                                   JsonSerializer.Serialize(existingProviderMetadata, Json),
                                   Json)
                               ?? [];
                    }
                    catch
                    {
                        break;
                    }
            }
        }

        return [];
    }

    private static object CreateCompactionToolInput(object? encryptedContent)
        => JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["encrypted_content"] = GetValueAsString(encryptedContent)
            },
            Json);

    private static CallToolResult CreateCompactionToolOutput(object? encryptedContent)
        => new()
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = GetValueAsString(encryptedContent)
                }
            ]
        };

    private static bool IsCompactionToolCall(AIToolCallContentPart toolPart)
        => toolPart.ProviderExecuted == true
           && (
               string.Equals(toolPart.ToolName, CompactionToolName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(toolPart.Title, CompactionToolName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(toolPart.Type, "compaction", StringComparison.OrdinalIgnoreCase)
           );

    private static Dictionary<string, object?> CreateCompactionMessageMetadata(
        string providerId,
        string? id,
        object? encryptedContent,
        object? rawOutput = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["responses.type"] = "compaction"
        };

        if (!string.IsNullOrWhiteSpace(id))
            metadata["id"] = id;

        if (rawOutput is not null)
            metadata["responses.raw_output"] = rawOutput;

        MergeProviderScopedEncryptedContentMetadata(metadata, providerId, encryptedContent);
        return metadata;
    }

    private static Dictionary<string, object?> CreateCompactionToolMetadata(
        string providerId,
        object? encryptedContent,
        object? rawOutput = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["responses.type"] = "compaction"
        };

        if (rawOutput is not null)
            metadata["responses.raw_output"] = rawOutput;

        MergeProviderScopedEncryptedContentMetadata(metadata, providerId, encryptedContent);
        return metadata;
    }

    private static AIToolCallContentPart CreateUnifiedCompactionToolPart(
        string providerId,
        string? id,
        object? encryptedContent,
        object? rawOutput = null)
        => new()
        {
            Type = "compaction",
            ToolCallId = id ?? Guid.NewGuid().ToString("N"),
            ToolName = CompactionToolName,
            Title = CompactionToolName,
            Input = CreateCompactionToolInput(encryptedContent),
            Output = CreateCompactionToolOutput(encryptedContent),
            State = "output-available",
            ProviderExecuted = true,
            Metadata = CreateCompactionToolMetadata(providerId, encryptedContent, rawOutput)
        };
}
