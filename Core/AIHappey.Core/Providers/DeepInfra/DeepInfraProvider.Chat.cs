using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // POST https://api.deepinfra.com/v1/openai/chat/completions
        await foreach (var update in _client.CompletionsStreamAsync(
            chatRequest,
            url: "v1/openai/chat/completions",
            cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }
}

