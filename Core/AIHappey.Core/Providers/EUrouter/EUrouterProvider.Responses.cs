using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.EUrouter;

public partial class EUrouterProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var response = await this.GetResponse(_client,
                   options, cancellationToken: cancellationToken);

        return await EnrichResponseWithGatewayCostAsync(response, options.Model, cancellationToken);
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, 
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var update in this.GetResponses(_client,
                            options,
                            cancellationToken: cancellationToken))
        {
            if (update is ResponseCompleted completed)
                await EnrichResponseWithGatewayCostAsync(completed.Response, options.Model, cancellationToken);

            yield return update;
        }
    }
}
