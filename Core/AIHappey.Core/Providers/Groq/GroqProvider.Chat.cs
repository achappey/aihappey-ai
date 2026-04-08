using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
         ChatRequest chatRequest,
         [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken: cancellationToken);

        switch (model.Type)
        {
            case "speech":
                {
                    await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
                        yield return p;

                    yield break;
                }
            case "transcription":
                {
                    await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                        yield return p;

                    yield break;
                }
            default:
                {
                    var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());

                    await foreach (var part in this.StreamUnifiedAsync(
                        unifiedRequest,
                        cancellationToken))
                    {
                        foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                        {
                            yield return uiPart;
                        }
                    }

                    yield break;
                }
        }
    }
}
