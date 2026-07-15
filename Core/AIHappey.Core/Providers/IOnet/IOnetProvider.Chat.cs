using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.IOnet;

public partial class IOnetProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                yield return await EnrichFinishPartWithGatewayCostAsync(uiPart, chatRequest.Model, cancellationToken);
            }
        }

        yield break;
    }
}
