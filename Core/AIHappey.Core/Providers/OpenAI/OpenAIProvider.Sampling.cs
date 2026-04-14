using AIHappey.Core.AI;
using AIHappey.Sampling.Mapping;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = chatRequest.GetModel();

        if (model?.Contains("image") == true)
        {
            return await this.ImageSamplingAsync(chatRequest,
                    cancellationToken: cancellationToken);
        }

        if (model?.Contains("tts") == true)
        {
            return await this.SpeechSamplingAsync(chatRequest,
                    cancellationToken: cancellationToken);
        }

        if (model?.Contains("search-preview") == true)
        {
            return await this.ChatCompletionsSamplingAsync(chatRequest, cancellationToken);
        }

        var result = await this.ExecuteUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToSamplingResult();
    }

}
