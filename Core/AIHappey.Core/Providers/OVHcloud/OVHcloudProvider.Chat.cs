using AIHappey.Core.AI;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.OVHcloud;

public partial class OVHcloudProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsImageModel(chatRequest.Model))
        {
            await foreach (var update in this.StreamImageAsync(chatRequest,
              cancellationToken: cancellationToken))
                yield return update;

            yield break;
        }

        if (IsTranscriptionModel(chatRequest.Model))
        {
            await foreach (var update in this.StreamTranscriptionAsync(chatRequest,
              cancellationToken: cancellationToken))
                yield return update;

            yield break;
        }

        if (IsSpeechModel(chatRequest.Model))
        {
            await foreach (var update in this.StreamSpeechAsync(chatRequest,
              cancellationToken: cancellationToken))
                yield return update;

            yield break;
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
