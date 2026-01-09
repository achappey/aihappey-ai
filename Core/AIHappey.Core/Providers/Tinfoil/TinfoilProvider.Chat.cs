using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Tinfoil;

public sealed partial class TinfoilProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // POST https://inference.tinfoil.sh/v1/chat/completions
        await foreach (var update in _client.CompletionsStreamAsync(
            chatRequest,
            url: "v1/chat/completions",
            cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }
}

