using AIHappey.Core.AI;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Scaleway;

public partial class ScalewayProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chatRequest.Model.Contains("voxtral") || chatRequest.Model.Contains("whisper"))
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

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

        yield break;
    }

}