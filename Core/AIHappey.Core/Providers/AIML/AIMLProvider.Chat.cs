using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIML;

public partial class AIMLProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var models = await ListModels(cancellationToken);
        var model = models.FirstOrDefault(a => a.Id == chatRequest.Model);

        switch (model?.Type)
        {
            case "image":
                {
                    await foreach (var update in this.StreamImageAsync(chatRequest,
                            cancellationToken: cancellationToken))
                        yield return update;


                    yield break;
                }

            case "speech":
                {
                    await foreach (var update in this.StreamSpeechAsync(chatRequest,
                            cancellationToken: cancellationToken))
                        yield return update;


                    yield break;
                }

            case "transcription":
                {
                    await foreach (var update in this.StreamTranscriptionAsync(chatRequest,
                            cancellationToken: cancellationToken))
                        yield return update;


                    yield break;
                }

            default:
                {
                    await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
                            cancellationToken: cancellationToken))
                        yield return update;


                    yield break;
                }


        }
    }
}