using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Vapi;

public partial class VapiProvider
{
    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => ResponsesAsyncInternal(options, cancellationToken);

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => ResponsesStreamingAsyncInternal(options, cancellationToken);
}

