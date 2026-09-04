using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;

namespace AIHappey.Core.Providers.HumeAI;

public partial class HumeAIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in StreamUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var part in update.Event.ToUIMessagePart(GetIdentifier()))
                yield return part;
    }
}
