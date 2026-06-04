using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.HumeAI;

public partial class HumeAIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in this.StreamSpeechAsync(chatRequest,
                            cancellationToken: cancellationToken))
            yield return update;


        yield break;
    }
}
