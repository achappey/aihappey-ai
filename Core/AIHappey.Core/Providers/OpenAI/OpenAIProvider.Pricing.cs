using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    private const decimal OpenAIWebSearchCostPerCall = 0.01m;

    private static Dictionary<string, object?> AddOpenAIWebSearchCallCost(
      Dictionary<string, object?>? existingMetadata,
      int completedWebSearchCallCount)
    {
        var metadata = existingMetadata is not null
            ? new Dictionary<string, object?>(existingMetadata)
            : [];

        if (completedWebSearchCallCount <= 0)
            return metadata;

        var webSearchCost = completedWebSearchCallCount * OpenAIWebSearchCostPerCall;

        if (!metadata.TryGetValue("gateway", out var gatewayObj)
            || gatewayObj is not Dictionary<string, object?> gateway)
        {
            gateway = TryConvertGatewayMetadata(gatewayObj) ?? [];
            metadata["gateway"] = gateway;
        }

        gateway["cost"] = (TryGetDecimal(gateway.TryGetValue("cost", out var existingCost) ? existingCost : null) ?? 0m)
                          + webSearchCost;

        return metadata;
    }

    
    public static ResponseResult EnrichResponseWithOpenAIGatewayCostForTests(
        ResponseResult response,
        IEnumerable<ResponseStreamPart>? streamParts = null,
        string? requestModel = null,
        string? requestServiceTier = null)
    {
        var effectiveModelId = string.IsNullOrWhiteSpace(response.Model)
            ? requestModel
            : response.Model;
        var effectiveServiceTier = string.IsNullOrWhiteSpace(response.ServiceTier)
            ? requestServiceTier
            : response.ServiceTier;

        var pricing = OpenAITieredPricingResolver.Resolve(
            effectiveModelId,
            effectiveServiceTier,
            ModelCostMetadataEnricher.GetTotalTokens(response.Usage));

        response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(
            response.Usage,
            response.Metadata,
            pricing);

        var streamWebSearchCallCount = 0;
        if (streamParts is not null)
        {
            var state = new OpenAiResponseStreamEnrichmentState();
            foreach (var streamPart in streamParts)
                CaptureCompletedWebSearchCall(streamPart, state);

            streamWebSearchCallCount = state.CompletedWebSearchCallCount;
        }

        response.Metadata = AddOpenAIWebSearchCallCost(
            response.Metadata,
            Math.Max(streamWebSearchCallCount, CountCompletedWebSearchCalls(response.Output)));

        return response;
    }

    public async Task<ResponseResult> EnrichResponseWithOpenAIImageResultsForTests(
        ResponseResult response,
        CancellationToken cancellationToken = default)
        => await EnrichResponseResultWithContainerFilesAsync(response, cancellationToken);

}
