using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Interactions.Mapping;
using AIHappey.Vercel.Mapping;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var streamEvent in this.StreamUnifiedAsync(
                           request.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            foreach (var result in streamEvent.Event.ToUIMessagePart(GetIdentifier()))
            {
                yield return result;
            }
        }
    }
}
