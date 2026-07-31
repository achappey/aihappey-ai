using System.Runtime.CompilerServices;
using AIHappey.Responses.Streaming;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;

namespace AIHappey.Core.Providers.MIMICXAI;

public partial class MIMICXAIProvider
{
    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => ExecuteResponsesUnifiedAsync(options, cancellationToken);

    private async Task<ResponseResult> ExecuteResponsesUnifiedAsync(ResponseRequest options, CancellationToken cancellationToken)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        return StreamResponsesUnifiedAsync(options, cancellationToken);
    }

    private async IAsyncEnumerable<ResponseStreamPart> StreamResponsesUnifiedAsync(ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var streamEvent in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            yield return streamEvent.ToResponseStreamPart();
    }
}
