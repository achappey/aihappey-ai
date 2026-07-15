using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.OpperAI;

public partial class OpperAIProvider
{
    internal static ResponseResult EnrichResponseResultWithGatewayCostForTests(ResponseResult response)
        => EnrichResponseResultWithGatewayCost(response);

    internal static ResponseStreamPart EnrichResponseStreamPartWithGatewayCostForTests(ResponseStreamPart part)
        => EnrichResponseStreamPartWithGatewayCost(part);

    private static ResponseResult EnrichResponseResultWithGatewayCost(ResponseResult response)
    {
        response.Metadata = ModelCostMetadataEnricher.AddCost(
            response.Metadata,
            TryGetOpperAITotalCost(response.Usage));

        return response;
    }

    private static ResponseStreamPart EnrichResponseStreamPartWithGatewayCost(ResponseStreamPart part)
    {
        if (part is ResponseCompleted { Response: not null } completed)
            EnrichResponseResultWithGatewayCost(completed.Response);

        return part;
    }

    private static decimal? TryGetOpperAITotalCost(object? usage)
    {
        if (usage is null)
            return null;

        try
        {
            var usageElement = usage switch
            {
                JsonElement json => json,
                _ => JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web)
            };

            if (usageElement.ValueKind != JsonValueKind.Object)
                return null;

            if (TryGetOpperAIProperty(usageElement, "cost", out var directCost)
                && TryGetOpperAIDecimal(directCost, out var parsedDirectCost))
            {
                return parsedDirectCost;
            }

            if (TryGetOpperAIProperty(usageElement, "opper", out var opper)
                && opper.ValueKind == JsonValueKind.Object
                && TryGetOpperAIProperty(opper, "cost", out var opperCost)
                && opperCost.ValueKind == JsonValueKind.Object
                && TryGetOpperAIProperty(opperCost, "total", out var totalCost)
                && TryGetOpperAIDecimal(totalCost, out var parsedTotalCost))
            {
                return parsedTotalCost;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool TryGetOpperAIDecimal(JsonElement element, out decimal value)
    {
        value = 0m;

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var parsed) => (value = parsed) >= 0 || parsed < 0,
            JsonValueKind.String when decimal.TryParse(
                element.GetString(),
                NumberStyles.Number | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out var parsed) => (value = parsed) >= 0 || parsed < 0,
            _ => false
        };
    }

  
}

