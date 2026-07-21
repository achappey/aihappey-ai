using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider
{
    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}