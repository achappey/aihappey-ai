using AIHappey.Responses;
using AIHappey.Responses.Mapping;

namespace AIHappey.Core.Providers.FishAudio;

public partial class FishAudioProvider
{

    private async Task<ResponseResult> ResponsesAsyncInternal(
        ResponseRequest options,
        CancellationToken cancellationToken)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();
}

