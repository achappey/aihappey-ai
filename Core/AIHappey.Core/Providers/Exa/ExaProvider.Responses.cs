using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Exa;

public partial class ExaProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken);

        return result.ToResponseResult();
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in StreamUnifiedAsync(unifiedRequest, cancellationToken))
            yield return part.ToResponseStreamPart();
    }
}
