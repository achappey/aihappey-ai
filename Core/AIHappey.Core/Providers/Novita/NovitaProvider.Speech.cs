using AIHappey.Core.AI;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider : IModelProvider
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

        return SpeechRequestAsyncTxt2Speech(request, cancellationToken); // <- your existing method (task_id + polling)
    }
}