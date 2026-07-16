using AIHappey.Core.AI;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Fireworks;

public partial class FireworksProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = GetIdentifier().GetModels().FirstOrDefault(a => a.Id.EndsWith(chatRequest.Model))
            ?? throw new ArgumentException(chatRequest.Model);

        if (model.Type == "transcription")
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        if (model.Type == "image")
        {
            await foreach (var p in this.StreamImageAsync(chatRequest, cancellationToken))
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
    }
}
