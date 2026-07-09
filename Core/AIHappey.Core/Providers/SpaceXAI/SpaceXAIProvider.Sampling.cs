using ModelContextProtocol.Protocol;
using AIHappey.Sampling.Mapping;

namespace AIHappey.Core.Providers.SpaceXAI;

public partial class SpaceXAIProvider
{
    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        
        var result = await this.ExecuteUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()),
                  cancellationToken);

        return result.ToSamplingResult();
    }

}
