using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.CARouter;

public partial class CARouterProvider
{
    public async Task<ResponseResult> ResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var response = await this.GetResponse(
            _client,
            options,
            cancellationToken: cancellationToken);

        return EnrichResponseWithCARouterCost(response);
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var update in this.GetResponses(
            _client,
            options,
            cancellationToken: cancellationToken))
        {
            if (update is ResponseCompleted completed)
                EnrichResponseWithCARouterCost(completed.Response);

            yield return update;
        }
    }

    private static ResponseResult EnrichResponseWithCARouterCost(ResponseResult response)
    {
        response.Metadata = ModelCostMetadataEnricher.AddCost(
            response.Metadata,
            ReadCARouterCost(response.NormalizedUsage));

        return response;
    }

    private static decimal? ReadCARouterCost(ResponseUsage? usage)
    {
        if (usage is null)
            return null;

        if (usage.AdditionalProperties is not null
            && usage.AdditionalProperties.TryGetValue("cost", out var cost)
            && TryReadCARouterDecimal(cost, out var parsedCost))
        {
            return parsedCost;
        }

        if (usage.Raw is { ValueKind: JsonValueKind.Object } raw
            && raw.TryGetProperty("cost", out cost)
            && TryReadCARouterDecimal(cost, out parsedCost))
        {
            return parsedCost;
        }

        return null;
    }

    private static bool TryReadCARouterDecimal(JsonElement value, out decimal result)
    {
        result = 0m;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out result),
            JsonValueKind.String => decimal.TryParse(
                value.GetString(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out result),
            _ => false
        };
    }
}
