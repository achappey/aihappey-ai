using AIHappey.Core.AI;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.FishAudio;

public partial class FishAudioProvider
{

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

