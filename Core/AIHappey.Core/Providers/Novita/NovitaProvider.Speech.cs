using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider
{
    private const string BaseUrl = "https://api.novita.ai/v3/";
    private const string TaskResultUrl = "https://api.novita.ai/v3/async/task-result?task_id=";

    public Task<SpeechResponse> SpeechRequest(
           SpeechRequest request,
           CancellationToken cancellationToken = default)
    {
        // One small switch: GLM-TTS is sync (binary), others can stay on your async task flow.
        if (IsGlmTtsModel(request.Model))
            return SpeechRequestGlmTts(request, cancellationToken);

        if (IsMiniMaxSpeechModel(request.Model))
            return SpeechRequestMiniMax(request, cancellationToken);

        if (IsFishSpeechModel(request.Model))
            return SpeechRequestAsyncTxt2SpeechFish(request, cancellationToken);

        return SpeechRequestAsyncTxt2Speech(request, cancellationToken); // <- your existing method (task_id + polling)
    }

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

}