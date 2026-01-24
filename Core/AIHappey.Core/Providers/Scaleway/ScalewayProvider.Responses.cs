using AIHappey.Responses.Streaming;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;

namespace AIHappey.Core.Providers.Scaleway;

public partial class ScalewayProvider 
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await _client.GetResponses(
                   options, ct: cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.GetResponsesUpdates(
           options,
           ct: cancellationToken);
    }
}
