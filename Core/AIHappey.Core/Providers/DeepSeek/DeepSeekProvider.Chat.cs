using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.DeepSeek;

public sealed partial class DeepSeekProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // DeepSeek is OpenAI Chat Completions compatible, but WITHOUT the /v1 prefix.
        // POST https://api.deepseek.com/chat/completions
        await foreach (var update in _client.CompletionsStreamAsync(
            chatRequest,
            url: "chat/completions",
            cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }
}

