using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Nvidia;

public partial class NvidiaProvider 
{

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetResponse(_client,
                   options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetResponses(_client,
           options,
           cancellationToken: cancellationToken);
    }
}
