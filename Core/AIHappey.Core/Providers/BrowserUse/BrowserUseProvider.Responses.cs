using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.BrowserUse;

public partial class BrowserUseProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var streamEvent in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            yield return streamEvent.ToResponseStreamPart();
    }
}
