using System.Globalization;
using System.Net.Mime;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Straico;

public partial class StraicoProvider
{
    private static readonly JsonSerializerOptions StraicoJson = new(JsonSerializerOptions.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static string NormalizeStraicoShortcutModel(string? model)
    {
        var local = model?.Trim() ?? string.Empty;
        const string prefix = "straico/";

        if (local.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            local = local[prefix.Length..];

        return local.Trim('/');
    }

    private static string ExtractStraicoLastUserTextPrompt(AIRequest request)
    {
        var userItem = (request.Input?.Items ?? [])
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase)
                                   && HasStraicoTextPart(item.Content));

        if (userItem is not null)
            return ExtractStraicoTextParts(userItem.Content);

        return string.Empty;
    }

    private static bool HasStraicoTextPart(IEnumerable<AIContentPart>? parts)
        => (parts ?? []).OfType<AITextContentPart>().Any(part => !string.IsNullOrWhiteSpace(part.Text));

    private static string ExtractStraicoTextParts(IEnumerable<AIContentPart>? parts)
        => string.Join("\n", (parts ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static FormUrlEncodedContent BuildStraicoPromptFormContent(string prompt, string? model, AIRequest request)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("prompt", prompt)
        };

        if (!string.IsNullOrWhiteSpace(model))
            fields.Add(new("model", model));

        var providerOptions = ExtractStraicoProviderOptions(request.Metadata);
        foreach (var optionName in new[] { "search_type", "k", "fetch_k", "lambda_mult", "score_threshold" })
        {
            if (TryGetStraicoProviderOptionString(providerOptions, optionName) is { Length: > 0 } optionValue)
                fields.Add(new(optionName, optionValue));
        }

        return new FormUrlEncodedContent(fields);
    }

    private static JsonElement? ExtractStraicoProviderOptions(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("straico", out var raw) || raw is null)
            return null;

        var element = raw switch
        {
            JsonElement json => json.Clone(),
            _ => JsonSerializer.SerializeToElement(raw, StraicoJson)
        };

        return element.ValueKind == JsonValueKind.Object ? element : null;
    }

    private static string? TryGetStraicoProviderOptionString(JsonElement? providerOptions, string propertyName)
    {
        if (providerOptions is not JsonElement element || element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static JsonDocument ParseStraicoPromptResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return JsonDocument.Parse("""
                {"success":true,"data":{"answer":""}}
                """);
        }

        return JsonDocument.Parse(body);
    }

    private AIResponse CreateStraicoPromptUnifiedResponse(
        AIRequest request,
        JsonElement root,
        string shortcutType,
        string shortcutId,
        string? baseModel,
        string prompt)
    {
        var data = ExtractStraicoPromptData(root);
        var text = ExtractStraicoPromptAnswer(data) ?? string.Empty;
        var rawMetadataKey = $"straico.{shortcutType}.raw";
        var referencesMetadataKey = $"straico.{shortcutType}.references";
        var metadata = new Dictionary<string, object?>
        {
            [$"straico.{shortcutType}"] = true,
            [$"straico.{shortcutType}.id"] = shortcutId,
            [$"straico.{shortcutType}.model"] = baseModel,
            [$"straico.{shortcutType}.prompt"] = prompt,
            [rawMetadataKey] = root.Clone(),
            [referencesMetadataKey] = CloneProperty(data, "references"),
            [$"straico.{shortcutType}.file_name"] = data.TryGetString("file_name"),
            [$"straico.{shortcutType}.coins_used"] = TryGetDecimal(data, "coins_used")
        };

        var usage = CreateStraicoUsage(data);
        if (usage is not null)
            metadata[$"straico.{shortcutType}.usage"] = usage.Value.Clone();

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model?.ToModelId(GetIdentifier())
                    ?? (baseModel is null
                        ? $"agent/{shortcutId}".ToModelId(GetIdentifier())
                        : $"rag/{shortcutId}/{baseModel}".ToModelId(GetIdentifier())),
            Status = IsStraicoPromptSuccess(root) ? "completed" : "failed",
            Usage = usage,
            Metadata = metadata,
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = text,
                                Metadata = new Dictionary<string, object?>
                                {
                                    [rawMetadataKey] = root.Clone(),
                                    [referencesMetadataKey] = CloneProperty(data, "references")
                                }
                            }
                        ],
                        Metadata = new Dictionary<string, object?>
                        {
                            [rawMetadataKey] = root.Clone(),
                            [referencesMetadataKey] = CloneProperty(data, "references")
                        }
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    [rawMetadataKey] = root.Clone(),
                    [referencesMetadataKey] = CloneProperty(data, "references")
                }
            }
        };
    }

    private static JsonElement ExtractStraicoPromptData(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                return data.Clone();

            if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
                return response.Clone();
        }

        return root.Clone();
    }

    private static string? ExtractStraicoPromptAnswer(JsonElement data)
        => data.TryGetString("answer")
           ?? data.TryGetString("response")
           ?? data.TryGetString("message")
           ?? data.TryGetString("text")
           ?? data.TryGetString("output");

    private static bool IsStraicoPromptSuccess(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False)
            return false;

        return true;
    }

    private static JsonElement? CreateStraicoUsage(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object)
            return usage.Clone();

        var coins = TryGetDecimal(data, "coins_used");
        if (coins is null)
            return null;

        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["coins_used"] = coins.Value,
            ["coinsUsed"] = coins.Value
        }, StraicoJson);
    }

    private IEnumerable<AIStreamEvent> CreateStraicoSyntheticTextStream(AIRequest request, AIResponse response, string rawMetadataKey)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var eventId = request.Id ?? $"straico_{Guid.NewGuid():N}";
        var text = ExtractStraicoResponseText(response);
        var providerMetadata = CreateStraicoProviderMetadata(response, rawMetadataKey);
        var metadata = response.Metadata;

        if (!string.IsNullOrEmpty(text))
        {
            yield return CreateStraicoStreamEvent(eventId, "text-start", new AITextStartEventData { ProviderMetadata = providerMetadata }, timestamp, metadata);
            yield return CreateStraicoStreamEvent(eventId, "text-delta", new AITextDeltaEventData { Delta = text, ProviderMetadata = providerMetadata }, DateTimeOffset.UtcNow, metadata);
            yield return CreateStraicoStreamEvent(eventId, "text-end", new AITextEndEventData { ProviderMetadata = providerMetadata }, DateTimeOffset.UtcNow, metadata);
        }

        yield return CreateStraicoStreamEvent(
            eventId,
            "finish",
            new AIFinishEventData
            {
                FinishReason = string.Equals(response.Status, "failed", StringComparison.OrdinalIgnoreCase) ? "error" : "stop",
                Model = response.Model ?? request.Model,
                MessageMetadata = AIFinishMessageMetadata.Create(
                    response.Model ?? request.Model ?? "straico",
                    DateTimeOffset.UtcNow,
                    response.Usage,
                    additionalProperties: new Dictionary<string, object?>
                    {
                        ["straico"] = response.Metadata?.TryGetValue(rawMetadataKey, out var raw) == true ? raw : null
                    })
            },
            DateTimeOffset.UtcNow,
            metadata);
    }

    private AIStreamEvent CreateStraicoStreamEvent(string id, string type, object data, DateTimeOffset timestamp, Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static string ExtractStraicoResponseText(AIResponse response)
        => string.Join("\n", (response.Output?.Items ?? [])
            .Where(item => string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static Dictionary<string, object>? CreateStraicoProviderMetadata(AIResponse response, string rawMetadataKey)
    {
        var metadata = new Dictionary<string, object>();

        if (response.Metadata?.TryGetValue(rawMetadataKey, out var raw) == true && raw is not null)
            metadata["raw"] = raw;

        if (response.Usage is not null)
            metadata["usage"] = response.Usage;

        return metadata.Count == 0 ? null : metadata;
    }

    private static JsonElement? CloneProperty(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var property)
            ? property.Clone()
            : null;

    private static decimal? TryGetDecimal(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }
}
