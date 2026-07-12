using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;

namespace AIHappey.Core.Providers.ARKLabs;

public partial class ARKLabsProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
       ChatRequest chatRequest,
       [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var model = await this.GetModel(chatRequest.Model, cancellationToken);

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
                break;
        }

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
    }

}

