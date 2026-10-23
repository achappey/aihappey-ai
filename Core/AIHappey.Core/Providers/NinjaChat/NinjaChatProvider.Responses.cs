using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.NinjaChat;

public partial class NinjaChatProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetResponse(_client,
             options,
             cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (IsNativeSearchModel(options.Model))
            return StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                .ToResponseStreamParts(cancellationToken);

        return this.GetResponses(_client,
             options,
             cancellationToken: cancellationToken);
    }
}
