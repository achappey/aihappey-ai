using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.GreenPT;

public partial class GreenPTProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        if (chatRequest.Model.StartsWith("green-s", StringComparison.OrdinalIgnoreCase))
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
