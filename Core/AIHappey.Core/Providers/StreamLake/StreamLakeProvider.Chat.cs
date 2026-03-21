using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.StreamLake;

public partial class StreamLakeProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            url: "v1/endpoints/chat/completions",
            cancellationToken: cancellationToken))
            yield return update;
    }
}
