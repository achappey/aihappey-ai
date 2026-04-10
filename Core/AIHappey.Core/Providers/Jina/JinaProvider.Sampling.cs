using ModelContextProtocol.Protocol;
using AIHappey.Sampling.Mapping;

namespace AIHappey.Core.Providers.Jina;

public partial class JinaProvider
{
    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var result = await this.ExecuteUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToSamplingResult();
    }
}