using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.ContextualAI;

public partial class ContextualAIProvider
{
    private static readonly JsonSerializerOptions ContextualAIJson = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private static string NormalizeContextualAIModel(string? model)
    {
        var local = model?.Trim() ?? string.Empty;
        const string prefix = "contextualai/";

        if (local.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            local = local[prefix.Length..];

        return local.Trim('/');
    }

    private static JsonElement? ExtractProviderOptions(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("contextualai", out var raw) || raw is null)
            return null;

        var element = raw switch
        {
            JsonElement json => json.Clone(),
            JsonObject jsonObject => JsonSerializer.SerializeToElement(jsonObject, ContextualAIJson),
            Dictionary<string, object?> dictionary => JsonSerializer.SerializeToElement(dictionary, ContextualAIJson),
            _ => JsonSerializer.SerializeToElement(raw, ContextualAIJson)
        };

        return element.ValueKind == JsonValueKind.Object ? element : null;
    }

    private static JsonObject JsonElementObjectToJsonObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return [];

        return JsonNode.Parse(element.GetRawText()) as JsonObject ?? [];
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string url, JsonObject payload)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(payload.ToJsonString(ContextualAIJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        return request;
    }

    private static JsonArray BuildContextualAIMessages(AIRequest request, bool includeSystem, bool includeKnowledge)
    {
        var messages = new JsonArray();

        if (includeSystem && !string.IsNullOrWhiteSpace(request.Instructions))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.Instructions
            });
        }

        foreach (var item in request.Input?.Items ?? [])
        {
            var role = string.IsNullOrWhiteSpace(item.Role) ? "user" : item.Role!;

            if (!includeSystem && string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!includeKnowledge && string.Equals(role, "knowledge", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = ExtractText(item.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            messages.Add(new JsonObject
            {
                ["role"] = NormalizeMessageRole(role, includeSystem, includeKnowledge),
                ["content"] = text
            });
        }

        if (messages.Count == 0 && !string.IsNullOrWhiteSpace(request.Input?.Text))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = request.Input!.Text
            });
        }

        if (messages.Count == 0)
            throw new ArgumentException("ContextualAI requests require at least one text message.", nameof(request));

        return messages;
    }

    private static string NormalizeMessageRole(string role, bool includeSystem, bool includeKnowledge)
    {
        if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            return "assistant";

        if (includeSystem && string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
            return "system";

        if (includeKnowledge && string.Equals(role, "knowledge", StringComparison.OrdinalIgnoreCase))
            return "knowledge";

        return "user";
    }

    private static string ExtractText(IEnumerable<AIContentPart>? parts)
        => string.Join("\n", (parts ?? [])
            .Select(part => part switch
            {
                AITextContentPart text => text.Text,
                AIReasoningContentPart reasoning => reasoning.Text,
                AIFileContentPart file when file.Data is string text => text,
                _ => null
            })
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private IEnumerable<AIStreamEvent> CreateSyntheticTextStream(AIRequest request, AIResponse response, string rawMetadataKey)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var eventId = request.Id ?? $"contextualai_{Guid.NewGuid():N}";
        var text = ExtractResponseText(response);
        var providerMetadata = CreateProviderMetadata(response, rawMetadataKey);

        if (!string.IsNullOrEmpty(text))
        {
            yield return CreateStreamEvent(eventId, "text-start", new AITextStartEventData { ProviderMetadata = providerMetadata }, timestamp, response.Metadata);
            yield return CreateStreamEvent(eventId, "text-delta", new AITextDeltaEventData { Delta = text, ProviderMetadata = providerMetadata }, DateTimeOffset.UtcNow, response.Metadata);
            yield return CreateStreamEvent(eventId, "text-end", new AITextEndEventData { ProviderMetadata = providerMetadata }, DateTimeOffset.UtcNow, response.Metadata);
        }

        yield return CreateStreamEvent(
            eventId,
            "finish",
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = response.Model ?? request.Model,
                MessageMetadata = AIFinishMessageMetadata.Create(
                    response.Model ?? request.Model ?? "contextualai",
                    DateTimeOffset.UtcNow,
                    response.Usage,
                    additionalProperties: new Dictionary<string, object?>
                    {
                        ["contextualai"] = response.Metadata?.TryGetValue(rawMetadataKey, out var raw) == true ? raw : null
                    })
            },
            DateTimeOffset.UtcNow,
            response.Metadata);
    }

    private AIStreamEvent CreateStreamEvent(string id, string type, object data, DateTimeOffset timestamp, Dictionary<string, object?>? metadata)
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

    private static string ExtractResponseText(AIResponse response)
        => string.Join("\n", (response.Output?.Items ?? [])
            .Where(item => string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static Dictionary<string, object>? CreateProviderMetadata(AIResponse response, string rawMetadataKey)
    {
        var metadata = new Dictionary<string, object>();

        if (response.Metadata?.TryGetValue(rawMetadataKey, out var raw) == true && raw is not null)
            metadata["raw"] = raw;

        if (response.Usage is not null)
            metadata["usage"] = response.Usage;

        return metadata.Count == 0 ? null : metadata;
    }

    private static void AddProviderOption(JsonObject payload, AIRequest request, string optionName)
    {
        if (payload.ContainsKey(optionName))
            return;

        if (request.Metadata?.GetProviderOption<JsonElement>("contextualai", optionName) is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } element)
            payload[optionName] = JsonNode.Parse(element.GetRawText());
    }

    private static JsonElement? CloneProperty(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var property)
            ? property.Clone()
            : null;
}
