using System.Globalization;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.BeastLabAI;

public partial class BeastLabAIProvider
{
    private static ChatCompletion EnrichChatCompletionWithBeastLabAICost(ChatCompletion response)
    {
        if (!TryGetBeastLabAICost(response, out var cost))
            return response;

        response.AdditionalProperties = AddGatewayCostMetadata(response.AdditionalProperties, cost);
        return response;
    }

    private static ChatCompletionUpdate EnrichChatCompletionUpdateWithBeastLabAIStreamCost(
        ChatCompletionUpdate update,
        ref decimal? latestStreamCost)
    {
        if (TryGetBeastLabAIStreamCost(update, out var cost))
            latestStreamCost = cost;

        if (!latestStreamCost.HasValue)
            return update;

        if (!HasFinishReason(update) && !HasUsage(update))
            return update;

        update.AdditionalProperties = AddGatewayCostMetadata(update.AdditionalProperties, latestStreamCost.Value);
        AddUsageCost(update, latestStreamCost.Value);
        return update;
    }

    private static AIResponse EnrichUnifiedResponseWithBeastLabAICost(AIResponse response)
    {
        if (!TryGetCost(response.Metadata, out var metadataCost)
            && !TryGetCost(response.Usage, out metadataCost))
        {
            return response;
        }

        return new AIResponse
        {
            ProviderId = response.ProviderId,
            Model = response.Model,
            Status = response.Status,
            Output = response.Output,
            Usage = response.Usage,
            Metadata = ModelCostMetadataEnricher.AddCost(response.Metadata, metadataCost)
        };
    }

    public static ChatCompletion EnrichChatCompletionWithBeastLabAICostForTests(ChatCompletion response)
        => EnrichChatCompletionWithBeastLabAICost(response);

    public static ChatCompletionUpdate EnrichChatCompletionUpdateWithBeastLabAIStreamCostForTests(
        ChatCompletionUpdate update,
        ref decimal? latestStreamCost)
        => EnrichChatCompletionUpdateWithBeastLabAIStreamCost(update, ref latestStreamCost);

    public static AIResponse EnrichUnifiedResponseWithBeastLabAICostForTests(AIResponse response)
        => EnrichUnifiedResponseWithBeastLabAICost(response);

    private static bool TryGetBeastLabAICost(ChatCompletion response, out decimal cost)
    {
        if (TryGetCost(response.AdditionalProperties, out cost))
            return true;

        return TryGetCost(response.Usage, out cost);
    }

    private static bool TryGetBeastLabAIStreamCost(ChatCompletionUpdate update, out decimal cost)
    {
        if (TryGetCost(update.AdditionalProperties, out cost))
            return true;

        return TryGetCost(update.Usage, out cost);
    }

    private static Dictionary<string, JsonElement>? AddGatewayCostMetadata(
        Dictionary<string, JsonElement>? additionalProperties,
        decimal cost)
    {
        var enrichedAdditionalProperties = additionalProperties is null
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>(additionalProperties);

        var existingMetadata = TryGetPropertyCaseInsensitive(enrichedAdditionalProperties, "metadata", out var metadataElement)
            ? ToObjectDictionary(metadataElement)
            : null;

        SetPropertyCaseInsensitive(
            enrichedAdditionalProperties,
            "metadata",
            JsonSerializer.SerializeToElement(
                ModelCostMetadataEnricher.AddCost(existingMetadata, cost),
                JsonSerializerOptions.Web));

        return enrichedAdditionalProperties;
    }

    private static void AddUsageCost(ChatCompletionUpdate update, decimal cost)
    {
        if (update.Usage is null)
            return;

        var usageElement = ToJsonElement(update.Usage);
        if (usageElement.ValueKind != JsonValueKind.Object || TryGetProperty(usageElement, "cost", out _))
            return;

        var usage = ToObjectDictionary(usageElement);
        usage["cost"] = cost;
        update.Usage = JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web);
    }

    private static bool HasUsage(ChatCompletionUpdate update)
    {
        if (update.Usage is null)
            return false;

        var usageElement = ToJsonElement(update.Usage);
        return usageElement.ValueKind == JsonValueKind.Object;
    }

    private static bool HasFinishReason(ChatCompletionUpdate update)
    {
        foreach (var choice in update.Choices ?? [])
        {
            var choiceElement = ToJsonElement(choice);
            if (choiceElement.ValueKind == JsonValueKind.Object
                && TryGetProperty(choiceElement, "finish_reason", out var finishReason)
                && finishReason.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(finishReason.GetString()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetCost(Dictionary<string, JsonElement>? source, out decimal cost)
    {
        cost = 0m;

        return source is not null
            && TryGetPropertyCaseInsensitive(source, "cost", out var costElement)
            && TryParseDecimal(costElement, out cost);
    }

    private static bool TryGetCost(Dictionary<string, object?>? source, out decimal cost)
    {
        cost = 0m;

        if (source is null)
            return false;

        foreach (var (key, value) in source)
        {
            if (string.Equals(key, "cost", StringComparison.OrdinalIgnoreCase)
                && TryParseDecimal(value, out cost))
            {
                return true;
            }

            if (string.Equals(key, "gateway", StringComparison.OrdinalIgnoreCase)
                && TryGetCostFromGateway(value, out cost))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetCost(object? source, out decimal cost)
    {
        cost = 0m;

        if (source is null)
            return false;

        if (TryParseDecimal(source, out cost))
            return true;

        var sourceElement = ToJsonElement(source);
        return sourceElement.ValueKind == JsonValueKind.Object
            && TryGetProperty(sourceElement, "cost", out var costElement)
            && TryParseDecimal(costElement, out cost);
    }

    private static bool TryGetCostFromGateway(object? source, out decimal cost)
    {
        cost = 0m;

        if (source is null)
            return false;

        var gatewayElement = ToJsonElement(source);
        return gatewayElement.ValueKind == JsonValueKind.Object
            && TryGetProperty(gatewayElement, "cost", out var costElement)
            && TryParseDecimal(costElement, out cost);
    }

    private static bool TryParseDecimal(object? value, out decimal cost)
    {
        cost = 0m;

        return value switch
        {
            decimal decimalValue => (cost = decimalValue) >= 0 || decimalValue < 0,
            double doubleValue => (cost = Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture)) >= 0 || doubleValue < 0,
            float floatValue => (cost = Convert.ToDecimal(floatValue, CultureInfo.InvariantCulture)) >= 0 || floatValue < 0,
            int intValue => (cost = intValue) >= 0 || intValue < 0,
            long longValue => (cost = longValue) >= 0 || longValue < 0,
            string stringValue => decimal.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out cost),
            JsonElement json => TryParseDecimal(json, out cost),
            _ => false
        };
    }

    private static bool TryParseDecimal(JsonElement element, out decimal cost)
    {
        cost = 0m;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out cost),
            JsonValueKind.String => decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out cost),
            _ => false
        };
    }

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

    private static void SetPropertyCaseInsensitive(
        Dictionary<string, JsonElement> target,
        string name,
        JsonElement value)
    {
        var existingKey = target.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        if (existingKey is not null)
            target.Remove(existingKey);

        target[name] = value;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static JsonElement ToJsonElement(object? value)
        => value switch
        {
            JsonElement json => json,
            _ => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web)
        };

    private static Dictionary<string, object?> ToObjectDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return [];

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), JsonSerializerOptions.Web) ?? [];
    }
}
