using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;

namespace AIHappey.Core.Providers.xAI;

public partial class XAIProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        this.SetDefaultResponseProperties(options);

        return await _client.GetResponses(
                   options, ct: cancellationToken);
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        this.SetDefaultResponseProperties(options);

        return _client.GetResponsesUpdates(
           options,
           ct: cancellationToken);
    }
}
