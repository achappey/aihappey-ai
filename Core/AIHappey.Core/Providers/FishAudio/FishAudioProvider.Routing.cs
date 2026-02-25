using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.FishAudio;

public partial class FishAudioProvider
{
    private async Task<CreateMessageResult> SamplingAsyncInternal(
        CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken)
    {
        var model = await this.GetModel(chatRequest.GetModel(), cancellationToken);

        if (model.Type == "speech")
            return await this.SpeechSamplingAsync(chatRequest, cancellationToken);

        throw new NotImplementedException();
    }

    private async Task<ResponseResult> ResponsesAsyncInternal(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        var model = await this.GetModel(options.Model, cancellationToken);

        if (model.Type == "speech")
            return await this.SpeechResponseAsync(options, cancellationToken);

        throw new NotImplementedException();
    }
}

