using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Nebius;

public sealed partial class NebiusProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // POST https://api.tokenfactory.nebius.com/v1/chat/completions
        await foreach (var update in _client.CompletionsStreamAsync(
            chatRequest,
            cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }
}

