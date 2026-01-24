using AIHappey.Responses;
using AIHappey.Responses.Extensions;

namespace AIHappey.Core.Providers.Nvidia;

public partial class NvidiaProvider 
{

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await _client.GetResponses(
                   options, ct: cancellationToken);
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.GetResponsesUpdates(
           options,
           ct: cancellationToken);
    }
}
