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

        return await _client.GetResponses(
                   options, ct: cancellationToken);
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        options.ParallelToolCalls ??= true;

        var pricing = await this.ResolveCatalogPricingForModelAsync(options.Model, cancellationToken);

        await foreach (var update in _client.GetResponsesUpdates(options, ct: cancellationToken))
        {
            if (update is ResponseCompleted completed)
            {
                completed.Response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(
                    completed.Response.Usage,
                    completed.Response.Metadata,
                    pricing);
            }

            yield return update;
        }
    }
}
