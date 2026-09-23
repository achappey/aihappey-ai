using System.Runtime.CompilerServices;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DevicAI;

public partial class DevicAIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
                           chatRequest.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken))
        foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            yield return uiPart;
    }
}
