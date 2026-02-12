using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Bytez;

public partial class BytezProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            url: "openai/v1/chat/completions",
            cancellationToken: cancellationToken))
            yield return update;
    }
}
