using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Fireworks;

public partial class FireworksProvider : IModelProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            cancellationToken: cancellationToken))
            yield return update;
    }
}