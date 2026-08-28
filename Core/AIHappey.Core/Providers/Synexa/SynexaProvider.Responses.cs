using System.Runtime.CompilerServices;
using AIHappey.Vercel.Extensions;
using AIHappey.Responses.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Synexa;

public partial class SynexaProvider
{
    public async Task<ResponseResult> ResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
            .ToResponseStreamParts(cancellationToken))
            yield return part;
    }
}
