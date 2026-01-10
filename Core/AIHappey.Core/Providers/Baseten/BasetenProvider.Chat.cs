using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Baseten;

public sealed partial class BasetenProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // POST https://inference.baseten.co/v1/chat/completions
        await foreach (var update in _client.CompletionsStreamAsync(
            chatRequest,
            url: "chat/completions",
            cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }
}

