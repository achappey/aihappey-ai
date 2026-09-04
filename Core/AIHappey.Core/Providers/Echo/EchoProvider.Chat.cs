using System.Runtime.CompilerServices;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Echo;

public sealed partial class EchoProvider
{
    private async IAsyncEnumerable<UIMessagePart> StreamEchoAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        await foreach (var streamEvent in StreamUnifiedAsync(
                           chatRequest.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken))
        {
            foreach (var part in streamEvent.Event.ToUIMessagePart(GetIdentifier()))
                yield return part;
        }
    }
}
