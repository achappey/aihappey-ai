using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.CanopyWave;

public partial class CanopyWaveProvider
{

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // CanopyWave is OpenAI-compatible; use the generic streaming implementation.
        // POST https://inference.canopywave.io/v1/chat/completions
        await foreach (var update in _client.CompletionsStreamAsync(
            chatRequest,
            cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }


}

