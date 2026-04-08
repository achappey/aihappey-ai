using AIHappey.Sampling.Mapping;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider
{
    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var result = await this.ExecuteUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToSamplingResult();
    }
}