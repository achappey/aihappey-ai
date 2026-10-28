using System.Runtime.CompilerServices;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RouterPlus;

public sealed partial class RouterPlusProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = chatRequest.ToUnifiedRequest(GetIdentifier());
        await foreach (var part in StreamUnifiedAsync(request, cancellationToken))
            foreach (var messagePart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return messagePart;
    }
}
