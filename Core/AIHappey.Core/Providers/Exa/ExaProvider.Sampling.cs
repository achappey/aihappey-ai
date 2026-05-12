using AIHappey.Sampling.Mapping;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Exa;

public partial class ExaProvider
{
    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken);

        return result.ToSamplingResult();
    }
}
