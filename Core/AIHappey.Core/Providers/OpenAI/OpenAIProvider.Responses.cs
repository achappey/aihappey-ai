using System.Net.Http.Headers;
using AIHappey.Core.AI;
using AIHappey.Responses.Extensions;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        options.ParallelToolCalls ??= true;

        var response = await _client.GetResponses(
                   options, ct: cancellationToken);

        var effectiveModelId = string.IsNullOrWhiteSpace(response.Model)
            ? options.Model
            : response.Model;
        var effectiveServiceTier = string.IsNullOrWhiteSpace(response.ServiceTier)
            ? options.ServiceTier
            : response.ServiceTier;
        var pricing = OpenAITieredPricingResolver.Resolve(
            effectiveModelId,
            effectiveServiceTier,
            ModelCostMetadataEnricher.GetTotalTokens(response.Usage));

        response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(
            response.Usage,
            response.Metadata,
            pricing);

        return response;
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        options.ParallelToolCalls ??= true;

        await foreach (var update in _client.GetResponsesUpdates(options, ct: cancellationToken))
        {
            if (update is ResponseCompleted completed)
            {
                var effectiveModelId = string.IsNullOrWhiteSpace(completed.Response.Model)
                    ? options.Model
                    : completed.Response.Model;
                var effectiveServiceTier = string.IsNullOrWhiteSpace(completed.Response.ServiceTier)
                    ? options.ServiceTier
                    : completed.Response.ServiceTier;
                var pricing = OpenAITieredPricingResolver.Resolve(
                    effectiveModelId,
                    effectiveServiceTier,
                    ModelCostMetadataEnricher.GetTotalTokens(completed.Response.Usage));

                completed.Response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(
                    completed.Response.Usage,
                    completed.Response.Metadata,
                    pricing);
            }

            yield return update;
        }
    }
}
