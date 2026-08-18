using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.SpaceXAI;

public partial class SpaceXAIProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var response = await this.GetResponse(_client,
                   options, cancellationToken: cancellationToken);

        return EnrichResponseWithGatewayCost(response);
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var update in this.GetResponses(_client, options, cancellationToken: cancellationToken))
        {
            if (update is ResponseCompleted completed)
            {
                EnrichResponseWithGatewayCost(completed.Response);
            }

            yield return update;
        }
    }

    private static ResponseResult EnrichResponseWithGatewayCost(ResponseResult response)
    {
        response.Metadata = ModelCostMetadataEnricher.AddCost(
            response.Metadata,
            GetGatewayCost(response.Usage));

        return response;
    }

    public static ResponseResult EnrichResponseWithGatewayCostForTests(ResponseResult response)
        => EnrichResponseWithGatewayCost(response);
}
