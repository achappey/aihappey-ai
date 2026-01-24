using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.GTranslate;

public partial class GTranslateProvider
{
    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var modelId = chatRequest.GetModel();
        ArgumentNullException.ThrowIfNullOrEmpty(modelId);

        return await TranslateSamplingAsync(chatRequest, modelId, cancellationToken);
    }
}
