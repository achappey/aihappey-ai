using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.LTX;

public partial class LTXProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in this.StreamVideoAsync(chatRequest,
                            cancellationToken: cancellationToken))
            yield return update;


        yield break;
    }
}
