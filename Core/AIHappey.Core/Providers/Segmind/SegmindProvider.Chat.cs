using AIHappey.Core.AI;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;

namespace AIHappey.Core.Providers.Segmind;

public partial class SegmindProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var streamEvent in StreamUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var part in streamEvent.Event.ToUIMessagePart(GetIdentifier()))
                yield return part;
    }
}
