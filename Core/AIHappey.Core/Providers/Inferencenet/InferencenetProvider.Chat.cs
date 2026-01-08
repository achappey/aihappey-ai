using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Inferencenet;

public partial class InferencenetProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // POST https://api.inference.net/v1/chat/completions
        await foreach (var update in _client.CompletionsStreamAsync(
            chatRequest,
            cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }
}

