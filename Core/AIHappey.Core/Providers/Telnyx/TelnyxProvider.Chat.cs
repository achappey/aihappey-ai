using System.Runtime.CompilerServices;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Telnyx;

public partial class TelnyxProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Chat with transcription models: treat "whisper*" and "distil-whisper/*" as STT.
        if (chatRequest.Model.Contains("whisper", StringComparison.OrdinalIgnoreCase)
            || chatRequest.Model.StartsWith("distil-whisper/", StringComparison.OrdinalIgnoreCase))
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
    }

}

