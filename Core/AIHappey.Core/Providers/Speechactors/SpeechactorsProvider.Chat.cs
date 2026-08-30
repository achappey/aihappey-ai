using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Speechactors;

public partial class SpeechactorsProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var streamEvent in StreamUnifiedAsync(
                           chatRequest.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            foreach (var part in streamEvent.Event.ToUIMessagePart(GetIdentifier()))
                yield return part;
        }
    }
}
