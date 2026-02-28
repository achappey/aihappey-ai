using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.WidnAI;

public partial class WidnAIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        ApplyAuthHeader();

        if (IsTranslationModelId(chatRequest.Model))
        {
            await foreach (var p in StreamTranslateAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            cancellationToken: cancellationToken))
            yield return update;
    }
}
