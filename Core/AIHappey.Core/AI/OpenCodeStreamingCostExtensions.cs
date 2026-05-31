using System.Globalization;
using System.Text.Json;
using AIHappey.Messages;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;

namespace AIHappey.Core.AI;

internal static class OpenCodeStreamingCostExtensions
{
    internal static bool IsOpenCodeProvider(string? providerId)
        => string.Equals(providerId, "opencode", StringComparison.OrdinalIgnoreCase);

    internal static bool TryGetOpenCodePingCost(MessageStreamPart part, out decimal cost)
    {
        cost = 0m;

        if (!string.Equals(part.Type, "ping", StringComparison.OrdinalIgnoreCase)
            || part.AdditionalProperties is null
            || !TryGetPropertyCaseInsensitive(part.AdditionalProperties, "cost", out var costElement))
        {
            return false;
        }

        return TryParseDecimal(costElement, out cost);
    }

    internal static bool TryGetOpenCodePingCost(ResponseStreamPart part, out decimal cost)
    {
        cost = 0m;

        if (part is not ResponseUnknownEvent unknown
            || !string.Equals(unknown.Type, "ping", StringComparison.OrdinalIgnoreCase)
            || unknown.Data is null
            || !TryGetPropertyCaseInsensitive(unknown.Data, "cost", out var costElement))
        {
            return false;
        }

        return TryParseDecimal(costElement, out cost);
    }

    internal static AIStreamEvent ApplyGatewayCostToFinishEvent(AIStreamEvent streamEvent, decimal? cost)
    {
        if (!cost.HasValue
            || !string.Equals(streamEvent.Event.Type, "finish", StringComparison.OrdinalIgnoreCase)
            || streamEvent.Event.Data is not AIFinishEventData finishData)
        {
            return streamEvent;
        }

        var messageMetadata = finishData.MessageMetadata?.ToDictionary() ?? [];

        var gateway = messageMetadata.TryGetValue("gateway", out var existingGateway)
            ? ToObjectDictionary(existingGateway)
            : [];

        gateway["cost"] = cost.Value;
        messageMetadata["gateway"] = gateway;

        var enrichedFinishData = new AIFinishEventData
        {
            FinishReason = finishData.FinishReason,
            MessageMetadata = AIFinishMessageMetadata.FromDictionary(
                messageMetadata,
                fallbackModel: finishData.Model,
                fallbackTimestamp: DateTimeOffset.UtcNow),
            Model = finishData.Model,
            CompletedAt = finishData.CompletedAt,
            InputTokens = finishData.InputTokens,
            OutputTokens = finishData.OutputTokens,
            TotalTokens = finishData.TotalTokens,
            SequenceNumber = finishData.SequenceNumber,
            Response = finishData.Response,
            StopSequence = finishData.StopSequence
        };

        return new AIStreamEvent
        {
            ProviderId = streamEvent.ProviderId,
            Event = new AIEventEnvelope
            {
                Type = streamEvent.Event.Type,
                Id = streamEvent.Event.Id,
                Timestamp = streamEvent.Event.Timestamp,
                Input = streamEvent.Event.Input,
                Output = streamEvent.Event.Output,
                Data = enrichedFinishData,
                Metadata = streamEvent.Event.Metadata
            },
            Metadata = streamEvent.Metadata
        };
    }

    private static Dictionary<string, object?> ToObjectDictionary(object? value)
    {
        if (value is Dictionary<string, object?> dict)
            return new Dictionary<string, object?>(dict, StringComparer.Ordinal);

        if (value is JsonElement json && json.ValueKind == JsonValueKind.Object)
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json.GetRawText(), JsonSerializerOptions.Web) ?? [];

        if (value is null)
            return [];

        try
        {
            var element = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
            return element.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), JsonSerializerOptions.Web) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static bool TryParseDecimal(JsonElement element, out decimal cost)
    {
        cost = 0m;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out cost),
            JsonValueKind.String => TryParseDecimal(element.GetString(), out cost),
            _ => TryParseDecimal(element.ToString(), out cost)
        };
    }

    private static bool TryParseDecimal(string? value, out decimal cost)
        => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out cost);

    private static bool TryGetPropertyCaseInsensitive(
        Dictionary<string, JsonElement> source,
        string name,
        out JsonElement value)
    {
        if (source.TryGetValue(name, out value))
            return true;

        foreach (var (key, itemValue) in source)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = itemValue;
                return true;
            }
        }

        value = default;
        return false;
    }
}
